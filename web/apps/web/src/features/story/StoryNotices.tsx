'use client';

/**
 * Story Mode's notifications on the right edge (rules in `notices.ts`): the colony's ongoing conditions (they clear
 * themselves), the transient messages (expiring, coalesced with a ×count, at most four, "+N more", Dismiss all, Esc), and the
 * Log (the last 200, filterable, a click jumps to the pawn or place) with an unread badge.
 */
import type { StoryAlert, StoryColonyState } from '@mineclaw/contracts';
import { AlertTriangle, History, Info, X } from 'lucide-react';
import { useEffect, useMemo, useReducer, useRef, useState } from 'react';
import { cn } from '@/lib/utils';
import {
  addMessage,
  conditions,
  dismiss,
  dismissAll,
  emptyNotices,
  expire,
  extend,
  ingestAlerts,
  visible,
  type Condition,
  type LogEntry,
  type MessageIn,
  type Notice,
  type NoticeLevel,
  type NoticeState,
} from './notices';
import { panel } from './StoryHud';

export interface JumpTarget {
  pawn: string | null;
  pos: [number, number, number] | null;
  alert: StoryAlert | null;
}

type Act =
  | { t: 'alerts'; alerts: readonly StoryAlert[]; now: number; logOpen: boolean }
  | { t: 'message'; m: MessageIn; now: number; logOpen: boolean }
  | { t: 'tick'; now: number; hold: boolean }
  | { t: 'extend'; now: number }
  | { t: 'dismiss'; key: string }
  | { t: 'all' }
  | { t: 'read' };

/** Test knob (mock mode): message lifetimes are scaled by this. */
let ttlScale = 1;

function reduce(s: NoticeState, a: Act): NoticeState {
  switch (a.t) {
    case 'alerts':
      return ingestAlerts(s, a.alerts, a.now, ttlScale, a.logOpen);
    case 'message':
      return addMessage(s, a.m, a.now, ttlScale, a.logOpen);
    case 'tick':
      return expire(s, a.now, a.hold);
    case 'extend':
      return extend(s, a.now);
    case 'dismiss':
      return dismiss(s, a.key);
    case 'all':
      return dismissAll(s);
    case 'read':
      return s.unread ? { ...s, unread: 0 } : s;
  }
}

const icon = (level: NoticeLevel, cls?: string) =>
  level === 'info' || level === 'ok' ? (
    <Info className={cn('mt-0.5 size-3.5 shrink-0 text-info', cls)} />
  ) : (
    <AlertTriangle className={cn('mt-0.5 size-3.5 shrink-0', level === 'danger' ? 'text-danger' : 'text-warn', cls)} />
  );
const edge = (level: NoticeLevel) => (level === 'danger' ? 'border-l-danger' : level === 'warn' ? 'border-l-warn' : 'border-l-info');

export function StoryNotices({
  state,
  colony,
  toast,
  onJump,
  onCondition,
}: {
  state: StoryColonyState | undefined;
  /** The colony shown (a new one starts a new stack). */
  colony: string | null;
  /** The page's own latest toast (logged, not stacked). */
  toast: { text: string; tone: 'ok' | 'warn' } | null;
  onJump: (t: JumpTarget) => void;
  onCondition: (c: Condition) => void;
}) {
  const [s, send] = useReducer(reduce, undefined, emptyNotices);
  const [logOpen, setLogOpen] = useState(false);
  const hold = useRef(false);
  const ref = useRef(s);
  useEffect(() => {
    ref.current = s;
  });

  // A colony change starts over (its alerts are new to this stack).
  const colonyRef = useRef(colony);
  useEffect(() => {
    if (colonyRef.current !== colony) {
      colonyRef.current = colony;
      send({ t: 'all' });
    }
  }, [colony]);
  useEffect(() => {
    if (state?.alerts?.length) send({ t: 'alerts', alerts: state.alerts, now: Date.now(), logOpen });
  }, [state?.alerts]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (toast) send({ t: 'message', m: { level: toast.tone === 'warn' ? 'warn' : 'ok', text: toast.text, stack: false, source: 'page' }, now: Date.now(), logOpen });
  }, [toast]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (logOpen) send({ t: 'read' });
  }, [logOpen, s.unread]);
  // Expiry: a light tick while anything shows.
  const any = s.stack.length > 0;
  useEffect(() => {
    if (!any) return;
    const id = setInterval(() => send({ t: 'tick', now: Date.now(), hold: hold.current }), 250);
    return () => clearInterval(id);
  }, [any]);

  // Tests read and drive the stack through this (mock mode only).
  useEffect(() => {
    if (process.env.NEXT_PUBLIC_MOCK !== '1') return;
    (window as unknown as { __storyNotices?: unknown }).__storyNotices = {
      ingest: (alerts: StoryAlert[]) => send({ t: 'alerts', alerts, now: Date.now(), logOpen: false }),
      stack: () => ref.current.stack.map((n) => ({ text: n.text, count: n.count, level: n.level })),
      log: () => ref.current.log.length,
      unread: () => ref.current.unread,
      setTtlScale: (k: number) => (ttlScale = k),
    };
  }, []);

  const conds = useMemo(() => conditions(state), [state]);
  const { shown, more } = visible(s);

  return (
    <div className="pointer-events-auto flex w-64 flex-col gap-1" data-testid="notices">
      {conds.length ? <ConditionList conds={conds} onCondition={onCondition} /> : null}
      <div className="flex justify-end gap-1">
        {shown.length ? (
          <button
            type="button"
            onClick={() => send({ t: 'all' })}
            className={cn(panel, 'px-2 py-0.5 text-[11px] text-muted hover:text-fg')}
            data-testid="dismiss-all"
          >
            Dismiss all
          </button>
        ) : null}
        <LogButton open={logOpen} unread={s.unread} onToggle={() => setLogOpen((o) => !o)} />
      </div>
      {shown.length ? (
        <ul
          aria-label="Messages"
          data-testid="alerts"
          tabIndex={-1}
          className="flex flex-col gap-1 outline-none"
          onMouseEnter={() => (hold.current = true)}
          onMouseLeave={() => {
            hold.current = false;
            send({ t: 'extend', now: Date.now() });
          }}
          onKeyDown={(e) => {
            if (e.key === 'Escape') {
              e.stopPropagation();
              send({ t: 'all' });
            }
          }}
        >
          {shown.map((n) => (
            <Message key={n.key} n={n} onPick={() => onJump(n)} onDismiss={() => send({ t: 'dismiss', key: n.key })} />
          ))}
        </ul>
      ) : null}
      {more ? (
        <button
          type="button"
          onClick={() => setLogOpen(true)}
          className={cn(panel, 'self-end px-2 py-0.5 text-[11px] text-muted hover:text-fg')}
          data-testid="more-messages"
        >
          +{more} more
        </button>
      ) : null}
      {logOpen ? <LogPanel log={s.log} onJump={onJump} onClose={() => setLogOpen(false)} /> : null}
    </div>
  );
}

/** The ongoing conditions, each a button (select its pawns, open its tab). */
function ConditionList({ conds, onCondition }: { conds: Condition[]; onCondition: (c: Condition) => void }) {
  return (
    <ul aria-label="Alerts" data-testid="conditions" className="flex flex-col gap-0.5">
      {conds.map((c) => (
        <li key={c.key}>
          <button
            type="button"
            onClick={() => onCondition(c)}
            data-condition={c.key}
            className={cn(panel, 'flex w-full items-start gap-1.5 border-l-4 px-2 py-1 text-left text-[11.5px] leading-snug', edge(c.level))}
          >
            {icon(c.level)}
            <span className="min-w-0">{c.text}</span>
          </button>
        </li>
      ))}
    </ul>
  );
}

/** The Log toggle with its unread count (99+). */
function LogButton({ open, unread, onToggle }: { open: boolean; unread: number; onToggle: () => void }) {
  return (
    <button
      type="button"
      onClick={onToggle}
      aria-pressed={open}
      aria-label={`Message log${unread ? `, ${unread} unread` : ''}`}
      className={cn(panel, 'relative flex items-center gap-1 px-2 py-0.5 text-[11px] text-muted hover:text-fg', open && 'text-fg')}
      data-testid="log-button"
    >
      <History className="size-3.5" />
      Log
      {unread ? (
        <span className="ml-0.5 rounded-full bg-accent px-1.5 text-[10px] font-semibold text-accent-fg" data-testid="log-unread">
          {unread > 99 ? '99+' : unread}
        </span>
      ) : null}
    </button>
  );
}

function Message({ n, onPick, onDismiss }: { n: Notice; onPick: () => void; onDismiss: () => void }) {
  return (
    <li
      className={cn(panel, 'group flex items-start gap-1.5 border-l-4 px-2 py-1.5 text-[11.5px] leading-snug', edge(n.level))}
      data-testid="message"
    >
      <button type="button" onClick={onPick} className="flex min-w-0 flex-1 items-start gap-1.5 text-left">
        {icon(n.level)}
        <span className="min-w-0">{n.text}</span>
        {n.count > 1 ? (
          <span className="ml-auto shrink-0 rounded bg-surface-2 px-1 text-[10px] font-semibold tabular-nums text-muted" data-testid="message-count">
            ×{n.count}
          </span>
        ) : null}
      </button>
      <button type="button" onClick={onDismiss} className="opacity-0 group-hover:opacity-100 focus:opacity-100" aria-label="Dismiss">
        <X className="size-3 text-muted" />
      </button>
    </li>
  );
}

const FILTERS: ReadonlyArray<{ id: 'all' | 'warn' | 'info'; label: string }> = [
  { id: 'all', label: 'All' },
  { id: 'warn', label: 'Problems' },
  { id: 'info', label: 'Events' },
];

function LogPanel({ log, onJump, onClose }: { log: readonly LogEntry[]; onJump: (t: JumpTarget) => void; onClose: () => void }) {
  const [filter, setFilter] = useState<'all' | 'warn' | 'info'>('all');
  const [q, setQ] = useState('');
  const list = log
    .filter((e) => (filter === 'all' ? true : filter === 'warn' ? e.level === 'warn' || e.level === 'danger' : e.level === 'info' || e.level === 'ok'))
    .filter((e) => !q || e.text.toLowerCase().includes(q.toLowerCase()))
    .slice()
    .reverse();
  return (
    <section
      className={cn(panel, 'flex max-h-[60vh] w-72 flex-col self-end p-2 text-[11.5px]')}
      aria-label="Message log"
      data-testid="message-log"
      onKeyDown={(e) => {
        if (e.key === 'Escape') {
          e.stopPropagation();
          onClose();
        }
      }}
    >
      <div className="mb-1.5 flex items-center gap-1">
        <span className="font-semibold">Log</span>
        <div className="ml-auto flex gap-0.5" role="group" aria-label="Filter">
          {FILTERS.map((f) => (
            <button
              key={f.id}
              type="button"
              aria-pressed={filter === f.id}
              onClick={() => setFilter(f.id)}
              className={cn('rounded px-1.5 py-0.5', filter === f.id ? 'bg-accent-soft text-accent' : 'text-muted hover:bg-surface-2')}
            >
              {f.label}
            </button>
          ))}
        </div>
        <button type="button" onClick={onClose} aria-label="Close the log" className="ml-1">
          <X className="size-3.5 text-muted" />
        </button>
      </div>
      <input
        value={q}
        onChange={(e) => setQ(e.target.value)}
        placeholder="Search"
        aria-label="Search the log"
        className="mb-1.5 rounded border border-border bg-surface-2 px-2 py-1 text-[11.5px] outline-none"
      />
      <ul className="-mr-1 flex min-h-0 flex-col gap-0.5 overflow-y-auto pr-1">
        {list.length ? (
          list.map((e) => {
            const jump = !!(e.pawn || e.pos || e.alert?.fix);
            return (
              <li key={e.id}>
                <button
                  type="button"
                  disabled={!jump}
                  onClick={() => onJump(e)}
                  className={cn('flex w-full items-start gap-1.5 rounded px-1 py-0.5 text-left', jump ? 'hover:bg-surface-2' : 'cursor-default')}
                  title={jump ? 'Show on the map' : undefined}
                >
                  {icon(e.level, 'size-3')}
                  <span className="min-w-0 flex-1">{e.text}</span>
                  {e.count > 1 ? <span className="shrink-0 text-[10px] text-muted tabular-nums">×{e.count}</span> : null}
                  <span className="shrink-0 text-[10px] text-subtle tabular-nums">
                    {new Date(e.at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                  </span>
                </button>
              </li>
            );
          })
        ) : (
          <li className="px-1 py-2 text-muted">Nothing yet</li>
        )}
      </ul>
    </section>
  );
}
