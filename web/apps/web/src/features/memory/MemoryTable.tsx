'use client';

/**
 * Server-paged, virtualised memory table: only the pages under the viewport are fetched (`offset`/`limit` 100), rows are
 * windowed with TanStack Virtual, and sorting happens on the server. Explorer-spec §17.9.
 */
import { useQueries } from '@tanstack/react-query';
import { toNumber, type MemoryKindKey, type MemoryListQuery, type MemoryPage, type MemoryRow, type MemorySort } from '@mineclaw/contracts';
import { useVirtualizer } from '@tanstack/react-virtual';
import { ArrowDown, ArrowUp } from 'lucide-react';
import { useEffect, useMemo, useRef } from 'react';
import { api } from '@/lib/api/client';
import { qk } from '@/lib/api/query';
import { ago, cn } from '@/lib/utils';
import { Badge, EmptyState, ErrorState, Skeleton } from '@/components/ui/primitives';

export const PAGE = 100;
const ROW_H = 40;

const COLUMNS: Array<{ key: MemorySort | 'preview'; label: string; className: string }> = [
  { key: 'name', label: 'Name', className: 'w-[22%] min-w-40' },
  { key: 'type', label: 'Type', className: 'w-28 hidden md:block' },
  { key: 'state', label: 'State', className: 'w-28 hidden sm:block' },
  { key: 'dim', label: 'Dim', className: 'w-24 hidden lg:block' },
  { key: 'conf', label: 'Conf.', className: 'w-16 hidden lg:block text-right' },
  { key: 'updated', label: 'Updated', className: 'w-28 hidden sm:block' },
  { key: 'preview', label: 'Text', className: 'flex-1 min-w-0' },
];

type Filters = Omit<MemoryListQuery, 'offset' | 'limit'>;

export function MemoryTable({
  kind,
  filters,
  onSort,
  selectedId,
  onOpen,
  onTotal,
}: {
  kind: MemoryKindKey;
  filters: Filters;
  onSort: (s: MemorySort) => void;
  selectedId: string | null;
  onOpen: (row: MemoryRow) => void;
  onTotal?: (n: number | null) => void;
}) {
  const scroller = useRef<HTMLDivElement>(null);
  const filterKey = JSON.stringify(filters);
  const first = useQueries({
    queries: [0].map((p) => ({
      queryKey: qk('memory.list', { kind }, { ...filters, offset: p * PAGE, limit: PAGE }),
      queryFn: ({ signal }: { signal: AbortSignal }) => api('memory.list', { params: { kind }, query: { ...filters, offset: p * PAGE, limit: PAGE }, signal }),
      staleTime: 30_000,
    })),
  })[0]!;
  const total = first.data?.total ?? 0;
  useEffect(() => {
    onTotal?.(first.data ? first.data.total : null);
  }, [first.data, onTotal]);
  useEffect(() => {
    // (newer Chromium returns a Promise from scrollTo; an effect must not return it)
    if (scroller.current) scroller.current.scrollTop = 0;
  }, [kind, filterKey]);

  const v = useVirtualizer({ count: total, getScrollElement: () => scroller.current, estimateSize: () => ROW_H, overscan: 12 });
  const items = v.getVirtualItems();
  const pages = useMemo(() => {
    const set = new Set<number>();
    for (const it of items) set.add(Math.floor(it.index / PAGE));
    return [...set].filter((p) => p > 0);
  }, [items]);
  const rest = useQueries({
    queries: pages.map((p) => ({
      queryKey: qk('memory.list', { kind }, { ...filters, offset: p * PAGE, limit: PAGE }),
      queryFn: ({ signal }: { signal: AbortSignal }) => api('memory.list', { params: { kind }, query: { ...filters, offset: p * PAGE, limit: PAGE }, signal }),
      staleTime: 30_000,
    })),
  });
  const byPage = new Map<number, MemoryPage>();
  if (first.data) byPage.set(0, first.data);
  pages.forEach((p, i) => {
    const d = rest[i]?.data;
    if (d) byPage.set(p, d);
  });
  const rowAt = (i: number): MemoryRow | undefined => byPage.get(Math.floor(i / PAGE))?.items[i % PAGE];

  if (first.error) return <ErrorState error={first.error} onRetry={() => void first.refetch()} />;

  return (
    <div className="flex h-full min-h-0 flex-col" role="table" aria-label="Memories" aria-rowcount={total}>
      <div role="row" className="flex shrink-0 items-center gap-3 border-b border-border bg-surface-2/60 px-4 text-xs font-medium text-muted">
        {COLUMNS.map((c) =>
          c.key === 'preview' ? (
            <div key={c.key} role="columnheader" className={cn('py-2.5', c.className)}>
              {c.label}
            </div>
          ) : (
            <button
              key={c.key}
              role="columnheader"
              aria-sort={filters.sort === c.key ? (filters.dir === 'asc' ? 'ascending' : 'descending') : 'none'}
              onClick={() => onSort(c.key as MemorySort)}
              className={cn('flex items-center gap-1 py-2.5 text-left hover:text-fg', c.className, c.key === 'conf' && 'justify-end')}
            >
              {c.label}
              {filters.sort === c.key ? filters.dir === 'asc' ? <ArrowUp className="size-3" /> : <ArrowDown className="size-3" /> : null}
            </button>
          ),
        )}
      </div>
      <div ref={scroller} className="min-h-0 flex-1 overflow-auto">
        {first.isPending ? (
          <div className="space-y-2 p-4">
            {Array.from({ length: 12 }, (_, i) => (
              <Skeleton key={i} className="h-6" />
            ))}
          </div>
        ) : total === 0 ? (
          <EmptyState title="Nothing matches" hint="Try a shorter search or clear the filters." />
        ) : (
          <div style={{ height: v.getTotalSize(), position: 'relative' }}>
            {items.map((it) => {
              const r = rowAt(it.index);
              return (
                <div
                  key={it.key}
                  role="row"
                  aria-rowindex={it.index + 1}
                  tabIndex={r ? 0 : -1}
                  onClick={() => r && onOpen(r)}
                  onKeyDown={(e) => {
                    if (r && (e.key === 'Enter' || e.key === ' ')) {
                      e.preventDefault();
                      onOpen(r);
                    }
                  }}
                  className={cn(
                    'absolute inset-x-0 flex cursor-pointer items-center gap-3 border-b border-border px-4 text-[13px] outline-none',
                    r && r.id === selectedId ? 'bg-accent-soft' : 'hover:bg-surface-2 focus-visible:bg-surface-2',
                  )}
                  style={{ top: it.start, height: ROW_H }}
                >
                  {r ? (
                    <>
                      <div role="cell" className={cn('truncate font-medium text-fg', COLUMNS[0]!.className)}>
                        {r.name}
                      </div>
                      <div role="cell" className={cn('truncate text-muted', COLUMNS[1]!.className)}>
                        {r.type ?? ''}
                      </div>
                      <div role="cell" className={cn('truncate', COLUMNS[2]!.className)}>
                        {r.state ? <Badge>{r.state}</Badge> : null}
                      </div>
                      <div role="cell" className={cn('truncate text-muted capitalize', COLUMNS[3]!.className)}>
                        {r.dim}
                      </div>
                      <div role="cell" className={cn('tabular text-muted', COLUMNS[4]!.className)}>
                        {r.conf !== null && r.conf !== undefined ? toNumber(r.conf).toFixed(2) : ''}
                      </div>
                      <div role="cell" className={cn('truncate text-muted', COLUMNS[5]!.className)}>
                        {ago(r.updated)}
                      </div>
                      <div role="cell" className={cn('truncate text-muted', COLUMNS[6]!.className)}>
                        {r.preview}
                      </div>
                    </>
                  ) : (
                    <Skeleton className="h-4 w-full" />
                  )}
                </div>
              );
            })}
          </div>
        )}
      </div>
    </div>
  );
}
