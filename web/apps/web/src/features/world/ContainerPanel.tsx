'use client';

/**
 * What is in a chest (furnace, hopper…) a viewer clicked: the container's own grid (27 / 54 slots, the furnace's layout…) and
 * how fresh the hive's knowledge is ("Seen 3 min ago by maid_chan_102", "Open right now by maid_chan_70", "Not opened yet by any
 * bot"). It polls `GET /api/world/container` every 3 s only while it is mounted and the tab is visible.
 *
 * `ContainerPanel` floats over a world stage (put it inside the stage so it stays in full screen); `ContainerContents` is the
 * same body without the frame, for Story Mode's chest inspector.
 */
import type { ContainerView } from '@mineclaw/contracts';
import { Package, X } from 'lucide-react';
import { useEffect, useState } from 'react';
import { ApiError } from '@/lib/api/client';
import { useApi } from '@/lib/api/query';
import { cn } from '@/lib/utils';
import { Skeleton } from '@/components/ui/primitives';
import { ContainerGrid } from '@/features/live/inventory/ContainerGrid';
import { CONTAINER_TITLES, containerTotals, freshnessText } from '@/features/live/inventory/containerLayout';

export const CONTAINER_POLL_MS = 3000;

/** A container block in a dimension (any block of it: either half of a double chest). */
export interface ContainerTarget {
  dim: number;
  x: number;
  y: number;
  z: number;
}

export const sameContainer = (a: ContainerTarget | null | undefined, b: ContainerTarget | null | undefined) =>
  !!a && !!b && a.dim === b.dim && a.x === b.x && a.y === b.y && a.z === b.z;

/** One container, polled while `enabled` (a panel shows it); paused while the tab is hidden. */
export function useContainer(t: ContainerTarget | null | undefined, enabled = true) {
  return useApi(
    'world.container',
    { query: t ? { dim: t.dim, x: t.x, y: t.y, z: t.z } : undefined },
    {
      enabled: !!t && enabled,
      refetchInterval: (q) =>
        q.state.error instanceof ApiError && q.state.error.status === 404 ? false : CONTAINER_POLL_MS,
      refetchIntervalInBackground: false,
      staleTime: 0,
    },
  );
}

/** Re-renders every 15 s so "3 min ago" keeps up between polls. */
function useNow(): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), 15_000);
    return () => clearInterval(id);
  }, []);
  return now;
}

export function containerTitle(v: Pick<ContainerView, 'kind'> | null | undefined): string {
  return v ? CONTAINER_TITLES[v.kind] : 'Container';
}

/** The grid and its freshness line (no frame). */
export function ContainerContents({
  target,
  className,
  heading,
}: {
  target: ContainerTarget;
  className?: string;
  /** Shown above the grid (the panel has its own title, Story's inspector says "Contents"). */
  heading?: boolean;
}) {
  const q = useContainer(target);
  const now = useNow();
  const v = q.data;
  const notFound = q.error instanceof ApiError && q.error.status === 404;
  return (
    <section
      className={cn('space-y-2', className)}
      data-testid="container-contents"
      data-source={v?.source}
      aria-label="Container contents"
    >
      {heading ? (
        <div className="flex items-center gap-1.5 text-[10.5px] font-semibold tracking-wide text-subtle uppercase">
          <Package className="size-3" /> Contents
          {v && v.source !== 'unknown' ? (
            <span className="ml-auto font-normal tracking-normal normal-case tabular-nums">
              {containerTotals(v).used} / {v.size} slots
            </span>
          ) : null}
        </div>
      ) : null}
      {v ? (
        <>
          <p
            className={cn('text-[12px]', v.source === 'live' ? 'text-ok' : 'text-muted')}
            data-testid="container-freshness"
            title={v.exact ? undefined : 'Laid out from item counts: the real slot order is not known'}
          >
            {freshnessText(v, now)}
            {!v.exact && v.source !== 'unknown' ? ' · counts only' : ''}
          </p>
          {v.source === 'unknown' ? (
            <p className="py-2 text-sm text-muted" data-testid="container-unknown">
              Not opened yet by any bot. Its contents show here once a bot opens it.
            </p>
          ) : (
            <div className={cn(v.source !== 'live' && 'opacity-95')}>
              <ContainerGrid view={v} />
            </div>
          )}
        </>
      ) : notFound ? (
        <p className="py-2 text-sm text-muted" data-testid="container-missing">
          There is no container here any more.
        </p>
      ) : q.error ? (
        <p className="py-2 text-sm text-danger">Could not load the contents.</p>
      ) : (
        <Skeleton className="h-[7.5rem] w-[20rem] max-w-full rounded-lg" />
      )}
    </section>
  );
}

/** The floating panel: title, position, the contents, close (button or Esc). */
export function ContainerPanel({
  target,
  onClose,
  className,
}: {
  target: ContainerTarget;
  onClose: () => void;
  className?: string;
}) {
  const q = useContainer(target);
  const v = q.data;
  // Esc closes the panel unless the world has the mouse (its own Esc frees it first).
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && !document.pointerLockElement) onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);
  const pos = v?.pos ?? [target.x, target.y, target.z];
  return (
    <aside
      className={cn(
        'flex max-w-[calc(100%-1.5rem)] flex-col overflow-hidden rounded-xl border border-border bg-surface shadow-pop',
        className,
      )}
      aria-label={`${containerTitle(v)} contents`}
      data-testid="container-panel"
      data-kind={v?.kind}
    >
      <div className="flex items-center gap-2 border-b border-border px-3.5 py-2.5">
        <Package className="size-4 text-muted" aria-hidden />
        <h2 className="min-w-0 flex-1 truncate text-sm font-semibold text-fg">{containerTitle(v)}</h2>
        <span className="font-mono text-[11px] text-subtle tabular-nums">{pos.join(', ')}</span>
        <button
          onClick={onClose}
          className="grid size-7 place-items-center rounded-md text-muted hover:bg-surface-2 hover:text-fg"
          aria-label="Close container"
        >
          <X className="size-4" />
        </button>
      </div>
      <div className="min-h-0 overflow-y-auto px-3.5 py-3">
        <ContainerContents target={target} />
      </div>
    </aside>
  );
}
