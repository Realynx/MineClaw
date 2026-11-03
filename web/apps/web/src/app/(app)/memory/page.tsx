'use client';

import { MEMORY_EDIT_KIND, MEMORY_REBUILD_KIND, toNumber, type DimFilter, type MemoryKindKey, type MemoryRecord, type MemorySort } from '@mineclaw/contracts';
import { Copy, Hammer, Network, PackageOpen, Pencil, Plus, Search, Trash2, UserRound, X } from 'lucide-react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { Suspense, useDeferredValue, useEffect, useState } from 'react';
import { Badge, Button, Card, ErrorState, Input, KeyValues, Select, Skeleton, SkeletonLines } from '@/components/ui/primitives';
import { Dialog, Drawer } from '@/components/ui/overlays';
import { EditForm } from '@/features/admin/EditForm';
import { RebuildDialog } from '@/features/admin/RebuildDialog';
import { MemoryTable } from '@/features/memory/MemoryTable';
import { useApi } from '@/lib/api/query';
import { useAuth } from '@/lib/auth';
import { cn, copyText, dateTime, num } from '@/lib/utils';

function RecordExtras({ record }: { record: MemoryRecord }) {
  if (record.kind === 'claims' || record.kind === 'beliefs') {
    if (!record.evidence.length) return null;
    return (
      <section>
        <h3 className="mb-2 text-[11px] font-semibold tracking-wide text-subtle uppercase">Evidence</h3>
        <ul className="space-y-1.5 text-xs">
          {record.evidence.map((e, i) => (
            <li key={i} className="flex gap-2 rounded-lg border border-border px-3 py-2">
              <Badge tone={e.polarity > 0 ? 'ok' : e.polarity < 0 ? 'danger' : 'neutral'}>{e.polarity > 0 ? 'for' : e.polarity < 0 ? 'against' : 'neutral'}</Badge>
              <span className="min-w-0 flex-1 text-fg">
                {e.kind}
                {e.note ? ` · ${e.note}` : ''}
              </span>
              <span className="tabular text-subtle">w {toNumber(e.weight).toFixed(2)}</span>
              <span className="text-subtle">{dateTime(e.at)}</span>
            </li>
          ))}
        </ul>
      </section>
    );
  }
  if (record.kind === 'chests') {
    return (
      <section>
        <h3 className="mb-2 text-[11px] font-semibold tracking-wide text-subtle uppercase">Contents</h3>
        <ul className="grid grid-cols-2 gap-1.5 text-xs">
          {record.contents.map((c, i) => (
            <li key={i} className="flex justify-between rounded-md bg-surface-2 px-2.5 py-1.5">
              <span className="text-fg">{c.name}</span>
              <span className="tabular text-muted">×{num(c.count)}</span>
            </li>
          ))}
        </ul>
      </section>
    );
  }
  return null;
}

function Detail({ kind, id, onClose }: { kind: MemoryKindKey; id: string; onClose: () => void }) {
  const { isAdmin } = useAuth();
  const d = useApi('memory.get', { params: { kind, id } });
  const [edit, setEdit] = useState<'update' | 'delete' | null>(null);
  return (
    <Drawer open onOpenChange={(o) => !o && onClose()} title={d.data?.row.name ?? id} subtitle={`${kind} · ${id}`} width="md">
      {d.isPending ? (
        <SkeletonLines lines={8} className="p-5" />
      ) : d.error ? (
        <ErrorState error={d.error} onRetry={() => void d.refetch()} />
      ) : (
        <div className="space-y-5 p-5">
          <KeyValues rows={d.data.props.map((p) => [p.label, p.value ? <span className="whitespace-pre-wrap">{p.value}</span> : null])} />
          <RecordExtras record={d.data.record} />
          <div className="flex flex-wrap gap-2">
            <Button size="sm" onClick={() => void copyText(d.data.props.map((p) => `${p.label}\t${p.value ?? ''}`).join('\n'))}>
              <Copy className="size-3.5" /> Copy row
            </Button>
            {d.data.row.raptorLeaf !== null && d.data.row.raptorLeaf !== undefined ? (
              <Link href={`/memory-activity?layout=cloud&node=${d.data.row.raptorLeaf}`} className="inline-flex h-7 items-center gap-1.5 rounded-md border border-border bg-surface px-2.5 text-xs font-medium text-fg shadow-card hover:bg-surface-2">
                <Network className="size-3.5" /> Show in memory activity (leaf #{d.data.row.raptorLeaf})
              </Link>
            ) : null}
            {d.data.people.map((p) => (
              <Link key={p} href={`/shards?export=person&people=${encodeURIComponent(p)}`} className="inline-flex h-7 items-center gap-1.5 rounded-md border border-border bg-surface px-2.5 text-xs font-medium text-fg shadow-card hover:bg-surface-2">
                <UserRound className="size-3.5" /> Export everything about {p}
              </Link>
            ))}
            {isAdmin && d.data.editKind ? (
              <>
                <Button size="sm" onClick={() => setEdit('update')}>
                  <Pencil className="size-3.5" /> Edit
                </Button>
                <Button size="sm" variant="ghost" className="text-danger" onClick={() => setEdit('delete')}>
                  <Trash2 className="size-3.5" /> Delete
                </Button>
              </>
            ) : null}
          </div>
          {edit && d.data.editKind ? (
            <Dialog open onOpenChange={(o) => !o && setEdit(null)} title={`${edit === 'delete' ? 'Delete' : 'Edit'} ${d.data.editKind}`} description={d.data.row.name}>
              <EditForm
                kind={d.data.editKind}
                op={edit}
                id={d.data.id}
                prefill={d.data.editPrefill ?? {}}
                onCancel={() => setEdit(null)}
                onDone={() => {
                  setEdit(null);
                  if (edit === 'delete') onClose();
                }}
              />
            </Dialog>
          ) : null}
        </div>
      )}
    </Drawer>
  );
}

function MemoryInner() {
  const params = useSearchParams();
  const router = useRouter();
  const { isAdmin } = useAuth();
  const kinds = useApi('memory.kinds');
  const kind = (params.get('kind') as MemoryKindKey | null) ?? 'facts';
  const selectedId = params.get('id');
  const sort = (params.get('sort') as MemorySort | null) ?? 'updated';
  const dir = (params.get('dir') as 'asc' | 'desc' | null) ?? 'desc';
  const dim = (params.get('dim') as DimFilter | null) ?? 'any';
  const state = params.get('state') ?? undefined;
  const [q, setQ] = useState(params.get('q') ?? '');
  const dq = useDeferredValue(q.trim());
  const [total, setTotal] = useState<number | null>(null);
  const [adding, setAdding] = useState(false);
  const [rebuild, setRebuild] = useState(false);
  const info = kinds.data?.kinds.find((k) => k.key === kind);
  const editKind = MEMORY_EDIT_KIND[kind];
  const rebuildKind = MEMORY_REBUILD_KIND[kind];

  const set = (patch: Record<string, string | null | undefined>) => {
    const sp = new URLSearchParams(params.toString());
    for (const [k, v] of Object.entries(patch)) {
      if (v === null || v === undefined || v === '') sp.delete(k);
      else sp.set(k, v);
    }
    router.replace(`/memory?${sp.toString()}`, { scroll: false });
  };

  // keep the search box in the URL (debounced)
  useEffect(() => {
    const t = setTimeout(() => {
      if ((params.get('q') ?? '') !== dq) set({ q: dq || null });
    }, 300);
    return () => clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [dq]);

  const groups = kinds.data ? [...new Set(kinds.data.kinds.map((k) => k.group))] : [];
  const exportHref = `/shards?export=filter&kind=${encodeURIComponent(rebuildKind ?? kind)}${dq ? `&q=${encodeURIComponent(dq)}` : ''}`;

  return (
    <div className="flex h-full min-h-0">
      {/* kinds */}
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

      <div className="flex min-w-0 flex-1 flex-col">
        <div className="flex flex-wrap items-center gap-2 border-b border-border bg-surface px-4 py-3">
          <Select aria-label="Kind" value={kind} onChange={(e) => set({ kind: e.target.value, id: null, state: null })} className="h-8 md:hidden">
            {kinds.data?.kinds.map((k) => (
              <option key={k.key} value={k.key}>
                {k.label}
              </option>
            ))}
          </Select>
          <div className="mr-1 hidden md:block">
            <h1 className="text-[15px] font-semibold text-fg">{info?.label ?? kind}</h1>
            <p className="tabular text-xs text-muted">{total === null ? '…' : `${num(total)} ${total === 1 ? 'row' : 'rows'}`}</p>
          </div>
          <div className="relative min-w-48 flex-1">
            <Search className="pointer-events-none absolute top-1/2 left-2.5 size-3.5 -translate-y-1/2 text-subtle" />
            <Input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Every word must match…" aria-label="Filter rows" className="h-8 pl-8" />
          </div>
          <Select aria-label="Dimension" value={dim} onChange={(e) => set({ dim: e.target.value === 'any' ? null : e.target.value })} className="h-8">
            <option value="any">Any dimension</option>
            <option value="overworld">Overworld</option>
            <option value="nether">Nether</option>
            <option value="none">No position</option>
          </Select>
          {info?.states.length ? (
            <Select aria-label="State" value={state ?? ''} onChange={(e) => set({ state: e.target.value || null })} className="h-8">
              <option value="">Any state</option>
              {info.states.map((s) => (
                <option key={s} value={s}>
                  {s}
                </option>
              ))}
            </Select>
          ) : null}
          {q || dim !== 'any' || state ? (
            <Button
              size="sm"
              variant="ghost"
              onClick={() => {
                setQ('');
                set({ q: null, dim: null, state: null });
              }}
            >
              <X className="size-3.5" /> Clear
            </Button>
          ) : null}
          <span className="flex-1" />
          <Link href={exportHref} className="inline-flex h-7 items-center gap-1.5 rounded-md px-2.5 text-xs font-medium text-muted hover:bg-surface-2 hover:text-fg">
            <PackageOpen className="size-3.5" /> Export
          </Link>
          {isAdmin && rebuildKind ? (
            <Button size="sm" variant="ghost" onClick={() => setRebuild(true)}>
              <Hammer className="size-3.5" /> Rebuild
            </Button>
          ) : null}
          {isAdmin && editKind ? (
            <Button size="sm" variant="primary" onClick={() => setAdding(true)}>
              <Plus className="size-3.5" /> Add
            </Button>
          ) : null}
        </div>
        <div className="min-h-0 flex-1 bg-surface">
          {kinds.error ? (
            <Card className="m-4">
              <ErrorState error={kinds.error} onRetry={() => void kinds.refetch()} />
            </Card>
          ) : kinds.isPending ? (
            <Skeleton className="m-4 h-64" />
          ) : (
            <MemoryTable
              kind={kind}
              filters={{ q: dq || undefined, sort, dir, dim, state }}
              onSort={(s) => set({ sort: s, dir: s === sort && dir === 'desc' ? 'asc' : null })}
              selectedId={selectedId}
              onOpen={(r) => set({ id: r.id })}
              onTotal={setTotal}
            />
          )}
        </div>
      </div>
      {selectedId ? <Detail kind={kind} id={selectedId} onClose={() => set({ id: null })} /> : null}
      {adding && editKind ? (
        <Dialog open onOpenChange={setAdding} title={`Add ${editKind}`}>
          <EditForm kind={editKind} op="add" onCancel={() => setAdding(false)} onDone={() => setAdding(false)} />
        </Dialog>
      ) : null}
      {rebuild && rebuildKind ? <RebuildDialog open onOpenChange={setRebuild} request={{ scope: 'kind', kind: rebuildKind }} label={`${info?.label ?? kind} (${rebuildKind})`} /> : null}
    </div>
  );
}

export default function MemoryPage() {
  return (
    <Suspense fallback={null}>
      <MemoryInner />
    </Suspense>
  );
}
