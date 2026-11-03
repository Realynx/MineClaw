'use client';

import { ChevronRight, Copy, Hammer, Layers, PackageOpen } from 'lucide-react';
import Link from 'next/link';
import { useState, type ReactNode } from 'react';
import { useApi } from '@/lib/api/query';
import { useAuth } from '@/lib/auth';
import { cn, copyText, dateTime, num } from '@/lib/utils';
import { Badge, Button, ErrorState, SkeletonLines } from '@/components/ui/primitives';
import { RebuildDialog } from '@/features/admin/RebuildDialog';

export function Crumbs({ crumbs, onPick }: { crumbs: Array<{ index: number; layer: number; label: string }>; onPick: (id: number) => void }) {
  return (
    <nav aria-label="Path in the tree" className="flex flex-wrap items-center gap-0.5 text-xs">
      {crumbs.map((c, i) => (
        <span key={c.index} className="inline-flex items-center gap-0.5">
          {i > 0 ? <ChevronRight className="size-3 text-subtle" /> : null}
          <button onClick={() => onPick(c.index)} className="max-w-48 truncate rounded px-1 py-0.5 text-muted hover:bg-surface-2 hover:text-fg">
            {c.label}
          </button>
        </span>
      ))}
    </nav>
  );
}

/** RAPTOR node detail: text, doc line, path, children and parents, leaves below, and the node actions. */
export function NodeDetail({ id, onNavigate, extra, className }: { id: number; onNavigate: (id: number) => void; extra?: ReactNode; className?: string }) {
  const { isAdmin } = useAuth();
  const d = useApi('raptor.node', { params: { id } });
  const [showLeaves, setShowLeaves] = useState(false);
  const leaves = useApi('raptor.leaves', { params: { id } }, { enabled: showLeaves });
  const [rebuild, setRebuild] = useState(false);

  if (d.isPending) return <SkeletonLines lines={8} className={cn('p-5', className)} />;
  if (d.error) return <ErrorState error={d.error} onRetry={() => void d.refetch()} className={className} />;
  const n = d.data;
  return (
    <div className={cn('space-y-4 p-5', className)}>
      <Crumbs crumbs={n.breadcrumb} onPick={onNavigate} />
      <div>
        <div className="mb-1.5 flex flex-wrap items-center gap-1.5">
          <Badge tone={n.node.isLeaf ? 'neutral' : 'accent'}>{n.node.kindLabel}</Badge>
          <span className="text-xs text-subtle">#{n.node.index}</span>
          {n.node.dirty ? <Badge tone="warn">dirty</Badge> : null}
          {n.placeholder ? <Badge tone="warn">placeholder</Badge> : null}
          {n.pinned ? <Badge tone="info">pinned</Badge> : null}
        </div>
        <h3 className="text-[15px] font-semibold text-fg">{n.title}</h3>
      </div>
      <p className="text-sm leading-relaxed whitespace-pre-wrap text-fg">{n.text}</p>
      <div className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted">
        <span>{num(n.node.tokens)} tokens</span>
        {!n.node.isLeaf ? <span>{num(n.node.leavesBelow)} leaves below</span> : null}
        {n.node.parents.length > 1 ? <span>{n.node.parents.length} parents</span> : null}
      </div>
      {n.doc ? (
        <div className="rounded-lg border border-border bg-surface-2/60 px-3 py-2 text-xs text-muted">
          <code className="font-mono text-fg">{n.doc.ref}</code> · {n.doc.kind}
          {n.doc.tag ? `/${n.doc.tag}` : ''} · {n.doc.source ?? 'unknown source'} · {dateTime(n.doc.updated)}
        </div>
      ) : null}
      {extra}
      <div className="flex flex-wrap gap-2">
        <Button size="sm" onClick={() => void copyText(n.text)}>
          <Copy className="size-3.5" /> Copy text
        </Button>
        {!n.node.isLeaf ? (
          <Button size="sm" onClick={() => setShowLeaves((s) => !s)} aria-expanded={showLeaves}>
            <Layers className="size-3.5" /> {showLeaves ? 'Hide leaves' : 'Show leaves under'}
          </Button>
        ) : null}
        <Link
          href={`/shards?export=subtree&node=${n.node.index}`}
          className="inline-flex h-7 items-center gap-1.5 rounded-md border border-border bg-surface px-2.5 text-xs font-medium text-fg shadow-card hover:bg-surface-2"
        >
          <PackageOpen className="size-3.5" /> Export subtree
        </Link>
        {isAdmin && !n.node.isLeaf ? (
          <Button size="sm" onClick={() => setRebuild(true)}>
            <Hammer className="size-3.5" /> Rebuild subtree
          </Button>
        ) : null}
      </div>
      {showLeaves ? (
        leaves.isPending ? (
          <SkeletonLines lines={4} />
        ) : leaves.data ? (
          <div className="rounded-lg border border-border">
            <div className="flex items-center justify-between border-b border-border px-3 py-2 text-xs text-muted">
              <span>
                {num(leaves.data.leaves.length)} leaves · {num(leaves.data.tokenSum)} tokens
              </span>
              <Button
                size="sm"
                variant="ghost"
                onClick={() => void copyText(leaves.data.leaves.map((l) => [l.index, l.docRef, l.kind, l.title, l.tokens].join('\t')).join('\n'))}
              >
                <Copy className="size-3.5" /> Copy TSV
              </Button>
            </div>
            <ul className="max-h-64 divide-y divide-border overflow-auto">
              {leaves.data.leaves.map((l) => (
                <li key={l.index}>
                  <button onClick={() => onNavigate(l.index)} className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-xs hover:bg-surface-2">
                    <span className="tabular w-10 text-subtle">#{l.index}</span>
                    <span className="min-w-0 flex-1 truncate text-fg">{l.title}</span>
                    <span className="tabular text-subtle">{l.tokens}</span>
                  </button>
                </li>
              ))}
            </ul>
          </div>
        ) : null
      ) : null}
      {n.children.length ? (
        <section>
          <h4 className="mb-1.5 text-[11px] font-semibold tracking-wide text-subtle uppercase">Children</h4>
          <ul className="space-y-0.5">
            {n.children.slice(0, 30).map((c) => (
              <li key={c.index}>
                <button onClick={() => onNavigate(c.index)} className="flex w-full items-center gap-2 rounded px-2 py-1 text-left text-xs hover:bg-surface-2">
                  <span className="text-subtle">L{c.layer}</span>
                  <span className="min-w-0 flex-1 truncate text-fg">{c.label}</span>
                  <span className="tabular text-subtle">{c.tokens}</span>
                </button>
              </li>
            ))}
          </ul>
        </section>
      ) : null}
      {n.parents.length > 1 ? (
        <section>
          <h4 className="mb-1.5 text-[11px] font-semibold tracking-wide text-subtle uppercase">Parents</h4>
          <ul className="space-y-0.5">
            {n.parents.map((c) => (
              <li key={c.index}>
                <button onClick={() => onNavigate(c.index)} className="flex w-full items-center gap-2 rounded px-2 py-1 text-left text-xs hover:bg-surface-2">
                  <span className="text-subtle">L{c.layer}</span>
                  <span className="min-w-0 flex-1 truncate text-fg">{c.label}</span>
                </button>
              </li>
            ))}
          </ul>
        </section>
      ) : null}
      {rebuild ? <RebuildDialog open onOpenChange={setRebuild} request={{ scope: 'node', node: n.node.index }} label={`subtree #${n.node.index}`} /> : null}
    </div>
  );
}
