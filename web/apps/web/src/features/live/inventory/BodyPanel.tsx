'use client';

/**
 * The panel a body opens when it is clicked in the Live hive's world view: who it is, its vitals and task, a link to its full
 * view, and its inventory (polled only while this panel is open). It floats over the world stage (inside it, so it stays in
 * fullscreen) as plain DOM: the world keeps its one WebGL context and its frame rate.
 */
import { ArrowUpRight, X } from 'lucide-react';
import Link from 'next/link';
import { useEffect } from 'react';
import { useStatus } from '@/lib/live/hooks';
import { cn } from '@/lib/utils';
import { KIND_LABEL, StatusDot, Vitals } from '../common';
import { InventorySection } from './InventoryPanel';

export function BodyPanel({ bot, onClose, className }: { bot: string; onClose: () => void; className?: string }) {
  const status = useStatus();
  const view = status.data?.bots.find((b) => b.name === bot || b.short === bot);
  const name = view?.name ?? bot;
  // Esc closes the panel unless the world has the mouse (its own Esc frees it first).
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && !document.pointerLockElement) onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);
  return (
    <aside
      className={cn(
        'flex w-[23rem] max-w-[calc(100%-1.5rem)] flex-col overflow-hidden rounded-xl border border-border bg-surface shadow-pop',
        className,
      )}
      aria-label={`${bot} panel`}
      data-testid="body-panel"
    >
      <BodyHeader bot={bot} name={name} view={view} onClose={onClose} />
      <div className="min-h-0 space-y-3 overflow-y-auto px-3.5 py-3">
        {view ? <BodySummary view={view} /> : null}
        <InventorySection bot={name} compact />
      </div>
    </aside>
  );
}

type View = NonNullable<ReturnType<typeof useStatus>['data']>['bots'][number];

/** The body's status, name and kind, a link to its full view, and the close button. */
function BodyHeader({ bot, name, view, onClose }: { bot: string; name: string; view: View | undefined; onClose: () => void }) {
  return (
    <div className="flex items-center gap-2 border-b border-border px-3.5 py-2.5">
      <StatusDot status={view?.status} />
      <h2 className="min-w-0 flex-1 truncate text-sm font-semibold text-fg">{name}</h2>
      {view?.kind ? <span className="text-xs text-subtle">{KIND_LABEL[view.kind]}</span> : null}
      <Link
        href={`/live/${encodeURIComponent(name)}`}
        className="grid size-7 place-items-center rounded-md text-muted hover:bg-surface-2 hover:text-fg"
        aria-label={`Open ${bot}`}
        title="Open the body's view"
      >
        <ArrowUpRight className="size-4" />
      </Link>
      <button
        onClick={onClose}
        className="grid size-7 place-items-center rounded-md text-muted hover:bg-surface-2 hover:text-fg"
        aria-label="Close panel"
      >
        <X className="size-4" />
      </button>
    </div>
  );
}

/** Vitals and the current task. */
function BodySummary({ view }: { view: View }) {
  return (
    <>
      <Vitals hp={view.hp ?? 0} food={view.food ?? 0} compact />
      {view.task ? <p className="truncate text-[13px] text-muted">{view.task}</p> : null}
    </>
  );
}
