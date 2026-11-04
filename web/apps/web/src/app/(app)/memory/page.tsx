'use client';

import { MEMORY_EDIT_KIND, MEMORY_REBUILD_KIND, toNumber, type MemoryKindKey, type MemoryRecord } from '@mineclaw/contracts';
import { Suspense, useDeferredValue, useEffect, useState, type ReactNode } from 'react';
import { Badge, Card, ErrorState, KeyValues, Skeleton, SkeletonLines } from '@/components/ui/primitives';
import { Dialog, Drawer } from '@/components/ui/overlays';
import { EditForm } from '@/features/admin/EditForm';
import { RebuildDialog } from '@/features/admin/RebuildDialog';
import { DetailActions, KindNav, MemoryToolbar, useMemoryParams } from '@/features/memory/MemoryBrowser';
import { MemoryTable } from '@/features/memory/MemoryTable';
import { useApi } from '@/lib/api/query';
import { useAuth } from '@/lib/auth';
import { dateTime, num } from '@/lib/utils';

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
          <DetailActions d={d.data} canEdit={isAdmin && !!d.data.editKind} onEdit={setEdit} />
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
  const { params, set, kind, selectedId, sort, dir, dim, state } = useMemoryParams();
  const { isAdmin } = useAuth();
  const kinds = useApi('memory.kinds');
  const [q, setQ] = useState(params.get('q') ?? '');
  const dq = useDeferredValue(q.trim());
  const [total, setTotal] = useState<number | null>(null);
  const info = kinds.data?.kinds.find((k) => k.key === kind);
  const editKind = MEMORY_EDIT_KIND[kind];
  const rebuildKind = MEMORY_REBUILD_KIND[kind];
  const admin = useAdminDialogs(isAdmin, editKind, rebuildKind);

  // keep the search box in the URL (debounced)
  useEffect(() => {
    const t = setTimeout(() => {
      if ((params.get('q') ?? '') !== dq) set({ q: dq || null });
    }, 300);
    return () => clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [dq]);

  return (
    <div className="flex h-full min-h-0">
      {/* kinds */}
      <KindNav kinds={kinds} kind={kind} set={set} />

      <div className="flex min-w-0 flex-1 flex-col">
        <MemoryToolbar
          kinds={kinds}
          kind={kind}
          info={info}
          total={total}
          q={q}
          setQ={setQ}
          dim={dim}
          state={state}
          set={set}
          exportHref={exportHrefOf(rebuildKind ?? kind, dq)}
          onRebuild={admin.onRebuild}
          onAdd={admin.onAdd}
        />
        <div className="min-h-0 flex-1 bg-surface">
          <MemoryBody kinds={kinds}>
            <MemoryTable
              kind={kind}
              filters={{ q: dq || undefined, sort, dir, dim, state }}
              onSort={(s) => set({ sort: s, dir: s === sort && dir === 'desc' ? 'asc' : null })}
              selectedId={selectedId}
              onOpen={(r) => set({ id: r.id })}
              onTotal={setTotal}
            />
          </MemoryBody>
        </div>
      </div>
      {selectedId ? <Detail kind={kind} id={selectedId} onClose={() => set({ id: null })} /> : null}
      <AddDialog editKind={admin.adding ? editKind : undefined} close={() => admin.setAdding(false)} onOpenChange={admin.setAdding} />
      <KindRebuild rebuildKind={admin.rebuild ? rebuildKind : undefined} label={info?.label ?? kind} onOpenChange={admin.setRebuild} />
    </div>
  );
}

/** The export page, filtered to this kind (as the shard export names it) and the search. */
const exportHrefOf = (kind: string, dq: string) => `/shards?export=filter&kind=${encodeURIComponent(kind)}${dq ? `&q=${encodeURIComponent(dq)}` : ''}`;

/** The admin's add and rebuild dialogs: open state, and the toolbar actions (null when the kind cannot be added / rebuilt). */
function useAdminDialogs(isAdmin: boolean, editKind: unknown, rebuildKind: unknown) {
  const [adding, setAdding] = useState(false);
  const [rebuild, setRebuild] = useState(false);
  return {
    adding,
    setAdding,
    rebuild,
    setRebuild,
    onAdd: isAdmin && editKind ? () => setAdding(true) : null,
    onRebuild: isAdmin && rebuildKind ? () => setRebuild(true) : null,
  };
}

/** Adding a row of the kind (when it is open and the kind is editable). */
function AddDialog({
  editKind,
  close,
  onOpenChange,
}: {
  editKind: React.ComponentProps<typeof EditForm>['kind'] | null | undefined;
  close: () => void;
  onOpenChange: (o: boolean) => void;
}) {
  if (!editKind) return null;
  return (
    <Dialog open onOpenChange={onOpenChange} title={`Add ${editKind}`}>
      <EditForm kind={editKind} op="add" onCancel={close} onDone={close} />
    </Dialog>
  );
}

/** Rebuilding the kind (when it is open and the kind rebuilds). */
function KindRebuild({ rebuildKind, label, onOpenChange }: { rebuildKind: string | null | undefined; label: string; onOpenChange: (o: boolean) => void }) {
  if (!rebuildKind) return null;
  return <RebuildDialog open onOpenChange={onOpenChange} request={{ scope: 'kind', kind: rebuildKind }} label={`${label} (${rebuildKind})`} />;
}

/** The table once the kinds have loaded (an error or a skeleton before). */
function MemoryBody({ kinds, children }: { kinds: ReturnType<typeof useApi<'memory.kinds'>>; children: ReactNode }) {
  if (kinds.error)
    return (
      <Card className="m-4">
        <ErrorState error={kinds.error} onRetry={() => void kinds.refetch()} />
      </Card>
    );
  if (kinds.isPending) return <Skeleton className="m-4 h-64" />;
  return children;
}

export default function MemoryPage() {
  return (
    <Suspense fallback={null}>
      <MemoryInner />
    </Suspense>
  );
}
