'use client';

import { toNumber, type GraphColorBy, type GraphLayout, type GraphNode } from '@mineclaw/contracts';
import { ListTree, Maximize2, Network, Search, X } from 'lucide-react';
import { useRouter, useSearchParams } from 'next/navigation';
import { Suspense, useCallback, useMemo, useRef, useState, type FormEvent } from 'react';
import { Badge, Button, EmptyState, ErrorState, Input, Kbd, Skeleton } from '@/components/ui/primitives';
import { Segmented, Tooltip } from '@/components/ui/overlays';
import { GraphCanvas, type GraphCanvasHandle, type NodeStyle } from '@/features/graph/GraphCanvas';
import { Crumbs, NodeDetail } from '@/features/raptor/NodeDetail';
import { TreeView } from '@/features/raptor/TreeView';
import { useApi } from '@/lib/api/query';

type ViewMode = 'graph' | 'tree';

function RaptorInner() {
  const params = useSearchParams();
  const router = useRouter();
  const view = (params.get('view') as ViewMode | null) ?? 'graph';
  const layout = (params.get('layout') as GraphLayout | null) ?? 'icicle';
  const colorBy = (params.get('color') as GraphColorBy | null) ?? 'kind';
  const focus = params.get('focus') ? Number(params.get('focus')) : undefined;
  const selected = params.get('node') ? Number(params.get('node')) : null;
  const hits = useMemo(() => new Set((params.get('hits') ?? '').split(',').filter(Boolean).map(Number)), [params]);
  const hitQuery = params.get('q');
  const canvas = useRef<GraphCanvasHandle>(null);
  const [goto, setGoto] = useState('');
  const [hover, setHover] = useState<{ n: GraphNode; x: number; y: number } | null>(null);

  const set = useCallback(
    (patch: Record<string, string | number | null | undefined>) => {
      const sp = new URLSearchParams(params.toString());
      for (const [k, v] of Object.entries(patch)) {
        if (v === null || v === undefined || v === '') sp.delete(k);
        else sp.set(k, String(v));
      }
      router.replace(`/raptor?${sp.toString()}`, { scroll: false });
    },
    [params, router],
  );

  const graph = useApi('raptor.graph', { query: { layout, colorBy, ...(focus !== undefined ? { focus } : {}) } }, { enabled: view === 'graph' });
  const selectedDetail = useApi('raptor.node', { params: { id: selected ?? 0 } }, { enabled: selected !== null });
  const reveal = useMemo(() => selectedDetail.data?.breadcrumb.slice(0, -1).map((c) => c.index), [selectedDetail.data]);

  const styleFor = useCallback(
    (n: GraphNode): NodeStyle => {
      const hit = hits.has(n.id);
      return {
        fill: n.color,
        alpha: hits.size && !hit ? 0.18 : 0.35 + toNumber(n.opacity) * 0.55,
        outline: hit ? 'var(--accent)' : null,
        glow: hit ? { color: '#f2d24b', strength: 0.8 } : null,
      };
    },
    [hits],
  );

  const onGoto = (e: FormEvent) => {
    e.preventDefault();
    const id = Number(goto.replace('#', ''));
    if (Number.isInteger(id) && id >= 0) {
      set({ node: id });
      canvas.current?.reveal(id);
    }
  };

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex flex-wrap items-center gap-3 border-b border-border bg-surface px-4 py-3 sm:px-6">
        <div className="mr-auto">
          <h1 className="text-lg font-semibold tracking-tight text-fg">RAPTOR tree</h1>
          <p className="text-xs text-muted">The summary tree the bot recalls through.</p>
        </div>
        <form onSubmit={onGoto} className="flex items-center gap-1.5">
          <Input value={goto} onChange={(e) => setGoto(e.target.value)} placeholder="Go to #node" aria-label="Go to node number" className="h-8 w-32" inputMode="numeric" />
        </form>
        <Segmented<ViewMode>
          label="View"
          size="sm"
          value={view}
          onChange={(v) => set({ view: v === 'graph' ? null : v })}
          options={[
            { value: 'graph', label: <><Network className="size-3.5" /> Graph</> },
            { value: 'tree', label: <><ListTree className="size-3.5" /> Tree</> },
          ]}
        />
        {view === 'graph' ? (
          <>
            <Segmented<GraphLayout>
              label="Layout"
              size="sm"
              value={layout}
              onChange={(v) => set({ layout: v === 'icicle' ? null : v })}
              options={[
                { value: 'icicle', label: 'Icicle' },
                { value: 'radial', label: 'Radial' },
                { value: 'nodelink', label: 'Node-link' },
              ]}
            />
            <Segmented<GraphColorBy>
              label="Colour by"
              size="sm"
              value={colorBy}
              onChange={(v) => set({ color: v === 'kind' ? null : v })}
              options={[
                { value: 'kind', label: 'Kind' },
                { value: 'time', label: 'Time' },
                { value: 'cluster', label: 'Cluster' },
              ]}
            />
          </>
        ) : null}
      </div>

      <div className="grid min-h-0 flex-1 grid-cols-1 lg:grid-cols-[minmax(0,1fr)_24rem]">
        <div className="relative flex min-h-[28rem] flex-col">
          {view === 'graph' ? (
            <>
              <div className="flex min-h-10 flex-wrap items-center gap-2 border-b border-border bg-surface px-4 py-1.5">
                {graph.data?.breadcrumb.length ? (
                  <>
                    <button onClick={() => set({ focus: null })} className="text-xs text-muted hover:text-fg">
                      all memory
                    </button>
                    <Crumbs crumbs={graph.data.breadcrumb} onPick={(id) => set({ focus: id })} />
                  </>
                ) : (
                  <span className="text-xs text-muted">all memory · double-click a cluster to focus it</span>
                )}
                {hits.size ? (
                  <Badge tone="warn" className="ml-auto">
                    <Search className="size-3" /> {hits.size} hits{hitQuery ? ` for “${hitQuery}”` : ''}
                    <button onClick={() => set({ hits: null, q: null })} aria-label="Clear search hits" className="ml-1">
                      <X className="size-3" />
                    </button>
                  </Badge>
                ) : null}
                <span className={hits.size ? '' : 'ml-auto'}>
                  <Tooltip content="Fit (F)">
                    <Button size="icon-sm" variant="ghost" aria-label="Fit" onClick={() => canvas.current?.fit()}>
                      <Maximize2 className="size-3.5" />
                    </Button>
                  </Tooltip>
                </span>
              </div>
              <div className="relative min-h-0 flex-1 bg-canvas">
                {graph.isPending ? (
                  <Skeleton className="absolute inset-6 rounded-xl" />
                ) : graph.error ? (
                  <ErrorState error={graph.error} onRetry={() => void graph.refetch()} className="absolute inset-0" />
                ) : graph.data.nodes.length === 0 ? (
                  <EmptyState title="No RAPTOR tree stored in this db" className="absolute inset-0" />
                ) : (
                  <GraphCanvas
                    ref={canvas}
                    graph={graph.data}
                    styleFor={styleFor}
                    selected={selected}
                    onSelect={(id) => set({ node: id })}
                    onFocus={(id) => set({ focus: id })}
                    onHover={(n, x, y) => setHover(n ? { n, x, y } : null)}
                    className="absolute inset-0"
                    label="RAPTOR graph"
                  />
                )}
                {graph.data ? (
                  <div className="pointer-events-none absolute bottom-3 left-3 flex flex-wrap gap-x-3 gap-y-1 rounded-lg border border-border bg-surface px-3 py-2 text-[11px] text-muted shadow-card">
                    {graph.data.legend.map((l) => (
                      <span key={l.key} className="inline-flex items-center gap-1.5">
                        <span className="size-2.5 rounded-sm" style={{ background: l.color }} /> {l.label}
                      </span>
                    ))}
                  </div>
                ) : null}
                <div className="pointer-events-none absolute right-3 bottom-3 hidden gap-1 text-[11px] text-subtle xl:flex">
                  <Kbd>↑↓←→</Kbd> walk <Kbd>Enter</Kbd> focus <Kbd>F</Kbd> fit <Kbd>Esc</Kbd> out
                </div>
                {hover ? (
                  <div className="pointer-events-none absolute z-10 max-w-72 rounded-lg border border-border bg-surface px-3 py-2 text-xs shadow-pop" style={{ left: hover.x + 14, top: hover.y + 14 }}>
                    <div className="font-medium text-fg">{hover.n.label}</div>
                    <div className="text-muted">
                      {hover.n.isLeaf ? hover.n.category : `L${hover.n.layer} · ${hover.n.weight} memories · ${hover.n.category}`}
                    </div>
                    {hover.n.terms.length && !hover.n.isLeaf ? <div className="text-subtle">{hover.n.terms.join(', ')}</div> : null}
                  </div>
                ) : null}
              </div>
            </>
          ) : (
            <div className="min-h-0 flex-1 overflow-auto bg-surface">
              <TreeView selected={selected} onSelect={(id) => set({ node: id })} reveal={reveal} />
            </div>
          )}
        </div>
        <aside className="min-h-0 overflow-auto border-t border-border bg-surface lg:border-t-0 lg:border-l">
          {selected !== null ? (
            <NodeDetail
              id={selected}
              onNavigate={(id) => {
                set({ node: id });
                canvas.current?.reveal(id);
              }}
            />
          ) : (
            <EmptyState
              icon={<Network className="size-5" />}
              title="Select a node"
              hint="Click a node to read its summary, walk its path, list the leaves under it, or export the subtree."
            />
          )}
        </aside>
      </div>
    </div>
  );
}

export default function RaptorPage() {
  return (
    <Suspense fallback={null}>
      <RaptorInner />
    </Suspense>
  );
}
