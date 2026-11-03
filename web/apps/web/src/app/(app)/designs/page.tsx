'use client';

import type { DesignDiffCell } from '@mineclaw/contracts';
import { useVirtualizer } from '@tanstack/react-virtual';
import { Boxes, Copy, Eye, Plus, Search, Sparkles } from 'lucide-react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { Suspense, useDeferredValue, useEffect, useRef, useState } from 'react';
import { Badge, Button, Card, EmptyState, ErrorState, Input, Select, Skeleton, SkeletonLines } from '@/components/ui/primitives';
import { apiUrl } from '@/lib/api/client';
import { useApi } from '@/lib/api/query';
import { ago, cn, copyText, dateTime } from '@/lib/utils';

function Cells({ cells }: { cells: DesignDiffCell[] }) {
  const ref = useRef<HTMLDivElement>(null);
  const v = useVirtualizer({ count: cells.length, getScrollElement: () => ref.current, estimateSize: () => 28, overscan: 10 });
  return (
    <div ref={ref} className="max-h-80 overflow-auto rounded-lg border border-border bg-code font-mono text-xs">
      <div style={{ height: v.getTotalSize(), position: 'relative' }}>
        {v.getVirtualItems().map((it) => {
          const c = cells[it.index]!;
          return (
            <div
              key={it.key}
              className={cn('absolute inset-x-0 flex items-center gap-3 px-3', c.op === '+' ? 'text-ok' : c.op === '-' ? 'text-danger' : 'text-warn')}
              style={{ top: it.start, height: 28 }}
            >
              <span className="w-3">{c.op === '-' ? '−' : c.op}</span>
              <span className="tabular w-28 text-muted">
                {c.x},{c.y},{c.z}
              </span>
              <span className="text-fg">{c.name}</span>
              {c.op === '~' ? (
                <span className="text-muted">
                  {c.from} → {c.to}
                </span>
              ) : null}
            </div>
          );
        })}
      </div>
    </div>
  );
}

function Detail({ id }: { id: number }) {
  const d = useApi('designs.get', { params: { id } });
  const [a, setA] = useState<string | null>(null);
  const [b, setB] = useState<string | null>(null);
  useEffect(() => {
    setA(null);
    setB(null);
  }, [id]);
  const from = a ?? d.data?.defaultDiff.a;
  const to = b ?? d.data?.defaultDiff.b;
  const diff = useApi('designs.diff', { params: { id }, query: { a: from, b: to } }, { enabled: !!from && !!to && from !== to });
  if (d.isPending) return <SkeletonLines lines={10} className="p-6" />;
  if (d.error) return <ErrorState error={d.error} onRetry={() => void d.refetch()} />;
  const des = d.data;
  return (
    <div className="space-y-5 p-5">
      <div>
        <div className="mb-1 flex flex-wrap items-center gap-2">
          <h2 className="text-lg font-semibold text-fg">
            <span className="text-subtle">#{des.design.id}</span> {des.design.name}
          </h2>
          <Badge tone={des.design.status === 'built' ? 'ok' : des.design.status === 'building' ? 'info' : 'neutral'}>{des.design.status}</Badge>
          {des.design.version ? <Badge tone="neutral" title="The game version it is drawn for">{des.design.version}</Badge> : null}
          <Link
            href={`/designs/studio?id=${des.design.id}`}
            className="ml-auto inline-flex h-7 items-center gap-1.5 rounded-md border border-border bg-surface px-2.5 text-xs text-fg hover:bg-surface-2"
          >
            <Sparkles className="size-3.5" /> Open in studio
          </Link>
        </div>
        <p className="text-xs text-muted">
          r{des.design.rev} · {des.design.revisions} revisions{des.design.requester ? ` · for ${des.design.requester}` : ''} · updated {ago(des.design.updated)}
        </p>
      </div>
      {des.design.hasPicture ? (
        // eslint-disable-next-line @next/next/no-img-element -- the bot's stored sheet, served as-is
        <img src={apiUrl('designs.picture', { id })} alt={`Preview sheet of ${des.design.name}`} className="w-full max-w-3xl rounded-lg border border-border bg-canvas" />
      ) : (
        <div className="grid h-40 max-w-3xl place-items-center rounded-lg border border-dashed border-border text-sm text-muted">No preview picture</div>
      )}
      {des.world ? (
        <p className="flex items-start gap-2 rounded-lg bg-info-soft px-3 py-2 text-sm text-info">
          <Eye className="mt-0.5 size-4 shrink-0" /> {des.world.text}
        </p>
      ) : null}
      <section>
        <h3 className="mb-2 text-xs font-semibold tracking-wide text-subtle uppercase">Diff</h3>
        <div className="mb-3 flex flex-wrap items-center gap-2 text-sm">
          <Select aria-label="From" value={from} onChange={(e) => setA(e.target.value)} className="h-8">
            {des.revisions.map((r) => (
              <option key={r.rev} value={String(r.rev)}>
                r{r.rev}
              </option>
            ))}
          </Select>
          <span className="text-muted">→</span>
          <Select aria-label="To" value={to} onChange={(e) => setB(e.target.value)} className="h-8">
            {des.revisions.map((r) => (
              <option key={r.rev} value={String(r.rev)}>
                r{r.rev}
              </option>
            ))}
            {des.world ? <option value="world">the world (last seen)</option> : null}
          </Select>
          {diff.data ? (
            <Button size="sm" variant="ghost" className="ml-auto" onClick={() => void copyText(diff.data.text)}>
              <Copy className="size-3.5" /> Copy diff
            </Button>
          ) : null}
        </div>
        {from === to ? (
          <p className="text-sm text-muted">Pick two different revisions.</p>
        ) : diff.isPending ? (
          <SkeletonLines lines={4} />
        ) : diff.error ? (
          <ErrorState error={diff.error} />
        ) : (
          <>
            <p className="mb-2 text-sm text-fg">{diff.data.summary}</p>
            <Cells cells={diff.data.cells} />
            {diff.data.truncated ? <p className="mt-1 text-xs text-subtle">Showing {diff.data.cells.length} of {diff.data.total} cells.</p> : null}
          </>
        )}
      </section>
      <section>
        <h3 className="mb-2 text-xs font-semibold tracking-wide text-subtle uppercase">Revisions</h3>
        <ol className="space-y-1.5">
          {des.revisions.map((r) => (
            <li key={r.rev} className="flex flex-wrap items-baseline gap-x-3 gap-y-0.5 rounded-lg border border-border px-3 py-2 text-sm">
              <span className="font-medium text-fg">r{r.rev}</span>
              {r.tag ? <Badge tone="accent">{r.tag}</Badge> : null}
              {r.version ? <span className="font-mono text-[11px] text-subtle">{r.version}</span> : null}
              <span className="min-w-0 flex-1 text-muted">“{r.message}” · {r.n} blocks</span>
              <span className="text-xs text-subtle">
                {r.author} ({r.authorKind}) · {dateTime(r.created)}
              </span>
            </li>
          ))}
        </ol>
      </section>
    </div>
  );
}

function DesignsInner() {
  const params = useSearchParams();
  const router = useRouter();
  const [q, setQ] = useState('');
  const dq = useDeferredValue(q.trim());
  const list = useApi('designs.list', { query: dq ? { q: dq } : {} });
  const id = params.get('id') ? Number(params.get('id')) : (list.data?.items[0]?.id ?? null);
  return (
    <div className="flex h-full min-h-0 flex-col lg:flex-row">
      <aside className="flex max-h-[40vh] shrink-0 flex-col border-b border-border bg-surface lg:max-h-none lg:w-80 lg:border-r lg:border-b-0">
        <div className="space-y-2.5 border-b border-border p-4">
          <div className="flex items-center gap-2">
            <h1 className="text-[15px] font-semibold text-fg">Designs</h1>
            <Link
              href="/designs/studio"
              className="ml-auto inline-flex h-7 items-center gap-1 rounded-md bg-accent px-2.5 text-xs font-medium text-accent-fg hover:bg-accent-strong"
              aria-label="New design"
            >
              <Plus className="size-3.5" /> New design
            </Link>
          </div>
          <div className="relative">
            <Search className="pointer-events-none absolute top-1/2 left-2.5 size-3.5 -translate-y-1/2 text-subtle" />
            <Input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Filter by name, status, requester…" aria-label="Filter designs" className="h-8 pl-8" />
          </div>
          {list.data ? (
            <p className="text-xs text-muted">
              {list.data.total} designs · {list.data.revisionsTotal} revisions
            </p>
          ) : null}
        </div>
        <ul className="min-h-0 flex-1 overflow-y-auto">
          {list.isPending ? (
            <li className="space-y-2 p-4">
              {Array.from({ length: 5 }, (_, i) => (
                <Skeleton key={i} className="h-12" />
              ))}
            </li>
          ) : list.error ? (
            <li>
              <ErrorState error={list.error} onRetry={() => void list.refetch()} />
            </li>
          ) : list.data.items.length === 0 ? (
            <li>
              <EmptyState title="No designs" />
            </li>
          ) : (
            list.data.items.map((d) => (
              <li key={d.id}>
                <button
                  onClick={() => router.replace(`/designs?id=${d.id}`, { scroll: false })}
                  aria-current={d.id === id ? 'true' : undefined}
                  className={cn('block w-full border-b border-border px-4 py-3 text-left', d.id === id ? 'bg-accent-soft' : 'hover:bg-surface-2')}
                >
                  <div className="flex items-center gap-2">
                    <span className="text-xs text-subtle">#{d.id}</span>
                    <span className="min-w-0 flex-1 truncate text-[13px] font-medium text-fg">{d.name}</span>
                    <Badge>{d.status}</Badge>
                  </div>
                  <p className="mt-0.5 text-[11px] text-subtle">
                    r{d.rev} · {d.revisions} revisions{d.version ? ` · ${d.version}` : ''}{d.requester ? ` · for ${d.requester}` : ''} · {ago(d.updated)}
                  </p>
                </button>
              </li>
            ))
          )}
        </ul>
      </aside>
      <section className="min-h-0 min-w-0 flex-1 overflow-auto bg-surface">
        {id ? (
          <Detail id={id} />
        ) : (
          <Card className="m-6">
            <EmptyState icon={<Boxes className="size-5" />} title="Pick a design" />
          </Card>
        )}
      </section>
    </div>
  );
}

export default function DesignsPage() {
  return (
    <Suspense fallback={null}>
      <DesignsInner />
    </Suspense>
  );
}
