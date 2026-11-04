'use client';

/**
 * The memory browser's parts (app/(app)/memory/page.tsx): its URL state, the kinds rail, the toolbar (kind, count, filter,
 * dimension, state, export, rebuild, add) and the selected row's actions.
 */
import type { DimFilter, MemoryKindKey, MemorySort } from '@mineclaw/contracts';
import { Copy, Hammer, Network, PackageOpen, Pencil, Plus, Search, Trash2, UserRound, X } from 'lucide-react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { Button, Input, Select, SkeletonLines } from '@/components/ui/primitives';
import type { useApi } from '@/lib/api/query';
import { cn, copyText, num } from '@/lib/utils';

type Kinds = ReturnType<typeof useApi<'memory.kinds'>>;
type KindInfo = NonNullable<Kinds['data']>['kinds'][number];
type MemoryGet = NonNullable<ReturnType<typeof useApi<'memory.get'>>['data']>;
export type SetParams = (patch: Record<string, string | null | undefined>) => void;

/** What the URL says: the kind, the open row, the sort and the filters; `set` patches it (null / undefined / '' removes). */
export function useMemoryParams() {
  const params = useSearchParams();
  const router = useRouter();
  const set: SetParams = (patch) => {
    const sp = new URLSearchParams(params.toString());
    for (const [k, v] of Object.entries(patch)) {
      if (v === null || v === undefined || v === '') sp.delete(k);
      else sp.set(k, v);
    }
    router.replace(`/memory?${sp.toString()}`, { scroll: false });
  };
  return {
    params,
    set,
    kind: (params.get('kind') as MemoryKindKey | null) ?? 'facts',
    selectedId: params.get('id'),
    sort: (params.get('sort') as MemorySort | null) ?? 'updated',
    dir: (params.get('dir') as 'asc' | 'desc' | null) ?? 'desc',
    dim: (params.get('dim') as DimFilter | null) ?? 'any',
    state: params.get('state') ?? undefined,
  };
}

/** The kinds rail, grouped (a kind that is not available is disabled). */
export function KindNav({ kinds, kind, set }: { kinds: Kinds; kind: MemoryKindKey; set: SetParams }) {
  const groups = kinds.data ? [...new Set(kinds.data.kinds.map((k) => k.group))] : [];
  return (
    <nav aria-label="Memory kinds" className="hidden w-56 shrink-0 overflow-y-auto border-r border-border bg-surface py-3 md:block">
      {kinds.isPending ? (
        <SkeletonLines lines={10} className="px-4" />
      ) : (
        groups.map((g) => (
          <div key={g} className="mb-3">
            <div className="px-4 pb-1 text-[11px] font-semibold tracking-wider text-subtle uppercase">{g}</div>
            {kinds.data!.kinds
              .filter((k) => k.group === g)
              .map((k) => (
                <button
                  key={k.key}
                  onClick={() => set({ kind: k.key, id: null, state: null, sort: null, dir: null })}
                  aria-current={k.key === kind ? 'page' : undefined}
                  disabled={!k.available}
                  className={cn(
                    'flex h-8 w-full items-center justify-between px-4 text-left text-[13px] disabled:opacity-40',
                    k.key === kind ? 'bg-accent-soft font-medium text-fg' : 'text-muted hover:bg-surface-2 hover:text-fg',
                  )}
                >
                  <span className="truncate">{k.label}</span>
                  <span className="tabular text-[11px] text-subtle">{num(k.count)}</span>
                </button>
              ))}
          </div>
        ))
      )}
    </nav>
  );
}

/** The state filter, for kinds that have states. */
function StateSelect({ states, state, set }: { states: readonly string[] | undefined; state: string | undefined; set: SetParams }) {
  if (!states?.length) return null;
  return (
    <Select aria-label="State" value={state ?? ''} onChange={(e) => set({ state: e.target.value || null })} className="h-8">
      <option value="">Any state</option>
      {states.map((s) => (
        <option key={s} value={s}>
          {s}
        </option>
      ))}
    </Select>
  );
}

const rowsText = (total: number | null) => (total === null ? '…' : `${num(total)} ${total === 1 ? 'row' : 'rows'}`);

/** The toolbar over the table. */
export function MemoryToolbar(p: {
  kinds: Kinds;
  kind: MemoryKindKey;
  info: KindInfo | undefined;
  total: number | null;
  q: string;
  setQ: (q: string) => void;
  dim: DimFilter;
  state: string | undefined;
  set: SetParams;
  exportHref: string;
  /** Admin: rebuild this kind (when it rebuilds) / add a row (when it is editable). */
  onRebuild: (() => void) | null;
  onAdd: (() => void) | null;
}) {
  const { set, info } = p;
  return (
    <div className="flex flex-wrap items-center gap-2 border-b border-border bg-surface px-4 py-3">
      <Select aria-label="Kind" value={p.kind} onChange={(e) => set({ kind: e.target.value, id: null, state: null })} className="h-8 md:hidden">
        {p.kinds.data?.kinds.map((k) => (
          <option key={k.key} value={k.key}>
            {k.label}
          </option>
        ))}
      </Select>
      <div className="mr-1 hidden md:block">
        <h1 className="text-[15px] font-semibold text-fg">{info?.label ?? p.kind}</h1>
        <p className="tabular text-xs text-muted">{rowsText(p.total)}</p>
      </div>
      <div className="relative min-w-48 flex-1">
        <Search className="pointer-events-none absolute top-1/2 left-2.5 size-3.5 -translate-y-1/2 text-subtle" />
        <Input value={p.q} onChange={(e) => p.setQ(e.target.value)} placeholder="Every word must match…" aria-label="Filter rows" className="h-8 pl-8" />
      </div>
      <Select aria-label="Dimension" value={p.dim} onChange={(e) => set({ dim: e.target.value === 'any' ? null : e.target.value })} className="h-8">
        <option value="any">Any dimension</option>
        <option value="overworld">Overworld</option>
        <option value="nether">Nether</option>
        <option value="none">No position</option>
      </Select>
      <StateSelect states={info?.states} state={p.state} set={set} />
      {p.q || p.dim !== 'any' || p.state ? (
        <Button
          size="sm"
          variant="ghost"
          onClick={() => {
            p.setQ('');
            set({ q: null, dim: null, state: null });
          }}
        >
          <X className="size-3.5" /> Clear
        </Button>
      ) : null}
      <span className="flex-1" />
      <Link href={p.exportHref} className="inline-flex h-7 items-center gap-1.5 rounded-md px-2.5 text-xs font-medium text-muted hover:bg-surface-2 hover:text-fg">
        <PackageOpen className="size-3.5" /> Export
      </Link>
      {p.onRebuild ? (
        <Button size="sm" variant="ghost" onClick={p.onRebuild}>
          <Hammer className="size-3.5" /> Rebuild
        </Button>
      ) : null}
      {p.onAdd ? (
        <Button size="sm" variant="primary" onClick={p.onAdd}>
          <Plus className="size-3.5" /> Add
        </Button>
      ) : null}
    </div>
  );
}

const pillLink =
  'inline-flex h-7 items-center gap-1.5 rounded-md border border-border bg-surface px-2.5 text-xs font-medium text-fg shadow-card hover:bg-surface-2';

/** A row's actions: copy it, show its RAPTOR leaf, export everything about a person it names, and (admin) edit / delete. */
export function DetailActions({ d, canEdit, onEdit }: { d: MemoryGet; canEdit: boolean; onEdit: (op: 'update' | 'delete') => void }) {
  const leaf = d.row.raptorLeaf;
  return (
    <div className="flex flex-wrap gap-2">
      <Button size="sm" onClick={() => void copyText(d.props.map((p) => `${p.label}\t${p.value ?? ''}`).join('\n'))}>
        <Copy className="size-3.5" /> Copy row
      </Button>
      {leaf !== null && leaf !== undefined ? (
        <Link href={`/memory-activity?layout=cloud&node=${leaf}`} className={pillLink}>
          <Network className="size-3.5" /> Show in memory activity (leaf #{leaf})
        </Link>
      ) : null}
      {d.people.map((p) => (
        <Link key={p} href={`/shards?export=person&people=${encodeURIComponent(p)}`} className={pillLink}>
          <UserRound className="size-3.5" /> Export everything about {p}
        </Link>
      ))}
      {canEdit ? (
        <>
          <Button size="sm" onClick={() => onEdit('update')}>
            <Pencil className="size-3.5" /> Edit
          </Button>
          <Button size="sm" variant="ghost" className="text-danger" onClick={() => onEdit('delete')}>
            <Trash2 className="size-3.5" /> Delete
          </Button>
        </>
      ) : null}
    </div>
  );
}
