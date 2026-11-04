'use client';

/**
 * The memory activity page's panels (app/(app)/memory-activity/page.tsx lays them out): the toolbar (whose activity, the
 * window, the layout), the cloud or the graph map with its legend and hover card, and the side panel (the selected node's
 * detail with its recent uses, or the most active regions).
 */
import type { CloudArrangement, GraphNode, MemoryAccessEvent } from '@mineclaw/contracts';
import { Activity, Loader2 } from 'lucide-react';
import type { ReactNode, RefObject } from 'react';
import { Segmented } from '@/components/ui/overlays';
import { EmptyState, ErrorState, Select, Skeleton } from '@/components/ui/primitives';
import { GraphCanvas, type GraphCanvasHandle, type NodeStyle } from '@/features/graph/GraphCanvas';
import type { CloudCanvasHandle } from './cloud/CloudCanvas';
import { CloudView } from './cloud/CloudView';
import { DEFAULT_ARRANGEMENT } from './cloud/cloudStyle';
import { brainColor, filterKey, stalenessColor, type ActivityFilter, type LastAccess, type Staleness } from './heat';
import { NodeDetail } from './NodeDetail';
import { WINDOWS, type CloudQuery, type View } from './activityState';
import type { useApi } from '@/lib/api/query';
import { ago, clock, duration } from '@/lib/utils';

/** The title row: whose activity, the window and the layout. */
export function ActivityToolbar(p: {
  filter: ActivityFilter;
  brains: readonly string[];
  bodies: readonly string[];
  windowMin: number;
  layout: View;
  inCtxCount: number;
  setParam: (k: string, v: string | null) => void;
}) {
  const { setParam } = p;
  return (
    <div className="flex flex-wrap items-center gap-3 border-b border-border bg-surface px-4 py-3 sm:px-6">
      <div className="mr-auto min-w-0">
        <h1 className="text-lg font-semibold tracking-tight text-fg">Memory activity</h1>
        <p className="text-xs text-muted">
          Lit by what each brain recalls. {p.inCtxCount} memories in context now.
        </p>
      </div>
      <label className="sr-only" htmlFor="who">
        Whose activity
      </label>
      <Select id="who" value={filterKey(p.filter)} onChange={(e) => setParam('who', e.target.value === 'all' ? null : e.target.value)} className="h-8 w-auto min-w-44">
        <option value="all">All brains</option>
        <optgroup label="Hive brains">
          {p.brains.map((b) => (
            <option key={b} value={filterKey({ kind: 'brain', brain: b } satisfies ActivityFilter)}>
              {b}
            </option>
          ))}
        </optgroup>
        <optgroup label="Bodies">
          {p.bodies.map((b) => (
            <option key={b} value={filterKey({ kind: 'body', body: b } satisfies ActivityFilter)}>
              {b}
            </option>
          ))}
        </optgroup>
      </Select>
      <Select aria-label="Window" value={p.windowMin} onChange={(e) => setParam('window', e.target.value)} className="h-8 w-auto">
        {WINDOWS.map((w) => (
          <option key={w} value={w}>
            Last {w} min
          </option>
        ))}
      </Select>
      <Segmented<View>
        label="Layout"
        size="sm"
        value={p.layout}
        onChange={(v) => setParam('layout', v === 'radial' ? null : v)}
        options={[
          { value: 'radial', label: 'Radial' },
          { value: 'icicle', label: 'Icicle' },
          { value: 'nodelink', label: 'Node-link' },
          { value: 'cloud', label: 'Cloud' },
        ]}
      />
    </div>
  );
}

/** What the cloud area shows before it has a layout: loading, an error, no tree, or the bridge laying it out. */
function CloudPending({ cloud }: { cloud: CloudQuery }) {
  if (cloud.isPending) return <Skeleton className="absolute inset-6 rounded-xl" />;
  if (cloud.error) return <ErrorState error={cloud.error} onRetry={() => void cloud.refetch()} className="absolute inset-0" />;
  const d = cloud.data;
  if (d.status === 'empty') return <EmptyState title="No RAPTOR tree yet" hint={d.note ?? undefined} className="absolute inset-0" />;
  const failed = d.status === 'error';
  return (
    <EmptyState
      icon={failed ? undefined : <Loader2 className="size-5 animate-spin" />}
      title={failed ? 'The memory cloud could not be laid out' : 'Laying out the memory cloud…'}
      hint={failed ? (d.note ?? undefined) : 'The bridge lays every memory out once per RAPTOR build; it takes a few seconds.'}
      className="absolute inset-0"
    />
  );
}

/** The memory cloud (one arrangement or a comparison), or its loading / empty state. */
export function CloudArea(p: {
  cloud: CloudQuery;
  hasCloud: boolean;
  view: Omit<React.ComponentProps<typeof CloudView>, 'onArrangement' | 'onCompare' | 'onClearHits' | 'canvasRef'>;
  compare: readonly CloudArrangement[] | null;
  setParams: (patch: Record<string, string | null>) => void;
  canvasRef: RefObject<CloudCanvasHandle | null>;
}) {
  const { compare, setParams } = p;
  return (
    <div className="relative min-h-0 flex-1">
      {p.hasCloud ? (
        <CloudView
          {...p.view}
          onArrangement={(a) => setParams({ arr: a === DEFAULT_ARRANGEMENT ? null : a, cmp: null })}
          onCompare={(list) =>
            setParams(
              list
                ? { cmp: list.join(',') }
                : { cmp: null, arr: compare?.[0] && compare[0] !== DEFAULT_ARRANGEMENT ? compare[0] : null },
            )
          }
          onClearHits={() => setParams({ hits: null, q: null })}
          canvasRef={p.canvasRef}
        />
      ) : (
        <CloudPending cloud={p.cloud} />
      )}
    </div>
  );
}

export type GraphQuery = ReturnType<typeof useApi<'raptor.graph'>>;

/** The graph map's body: loading, an error, no tree, or the canvas. */
function GraphBody({ graph, canvasRef, ...rest }: {
  graph: GraphQuery;
  canvasRef: RefObject<GraphCanvasHandle | null>;
  styleFor: (n: GraphNode) => NodeStyle;
  selected: number | null;
  onSelect: (id: number | null) => void;
  onHover: (n: GraphNode | null, x: number, y: number) => void;
  frameKey: number;
}) {
  if (graph.isPending) return <Skeleton className="absolute inset-6 rounded-xl" />;
  if (graph.error) return <ErrorState error={graph.error} onRetry={() => void graph.refetch()} className="absolute inset-0" />;
  if (graph.data.nodes.length === 0) return <EmptyState title="No RAPTOR tree yet" className="absolute inset-0" />;
  return (
    <GraphCanvas
      ref={canvasRef}
      graph={graph.data}
      styleFor={rest.styleFor}
      selected={rest.selected}
      onSelect={rest.onSelect}
      onHover={rest.onHover}
      frameKey={rest.frameKey}
      label="Memory activity map"
      className="absolute inset-0"
    />
  );
}

/** The map's legend: each brain's glow, the staleness rings, the half-life. */
function GraphLegend({ brains, hl }: { brains: readonly string[]; hl: number }) {
  return (
    <div className="pointer-events-none absolute top-3 left-3 max-w-[16rem] space-y-2 rounded-lg border border-border bg-surface p-3 text-[11px] text-muted shadow-card">
      <div className="flex flex-wrap gap-x-3 gap-y-1">
        {brains.map((b) => (
          <span key={b} className="inline-flex items-center gap-1.5">
            <span className="size-2.5 rounded-full" style={{ background: brainColor(b, brains), boxShadow: `0 0 6px ${brainColor(b, brains)}` }} />
            {b}
          </span>
        ))}
      </div>
      <div className="flex items-center gap-3">
        <span className="inline-flex items-center gap-1.5">
          <span className="size-2.5 rounded-sm border-2" style={{ borderColor: stalenessColor(0) }} /> fresh
        </span>
        <span className="inline-flex items-center gap-1.5">
          <span className="size-2.5 rounded-sm border-2" style={{ borderColor: stalenessColor(600) }} /> stale
        </span>
        <span className="inline-flex items-center gap-1.5">
          <span className="size-2.5 rounded-sm border border-dashed border-subtle" /> evicted
        </span>
      </div>
      <div>Brightness = recent use (half-life {duration(hl)})</div>
    </div>
  );
}

/** The hovered node: its label, size, last use and its context staleness. */
function HoverCard({
  hover,
  last,
  stale,
}: {
  hover: { n: GraphNode; x: number; y: number };
  last: ReadonlyMap<number, LastAccess>;
  stale: ReadonlyMap<number, Staleness>;
}) {
  const used = last.get(hover.n.id);
  const s = stale.get(hover.n.id);
  return (
    <div
      className="pointer-events-none absolute z-10 max-w-72 rounded-lg border border-border bg-surface px-3 py-2 text-xs shadow-pop"
      style={{ left: Math.min(hover.x + 14, 9999), top: hover.y + 14 }}
    >
      <div className="font-medium text-fg">{hover.n.label}</div>
      <div className="text-muted">
        {hover.n.isLeaf ? 'memory' : `L${hover.n.layer} · ${hover.n.weight} memories`}
        {used ? ` · used ${ago(used.at)} by ${used.brain}` : ' · not used in this window'}
      </div>
      {s ? (
        <div className="text-muted">{s.evicted ? 'evicted from context' : `in context, refreshed ${duration(s.stalenessS)} ago`}</div>
      ) : null}
    </div>
  );
}

/** The graph map with its legend and hover card. */
export function GraphArea(
  p: React.ComponentProps<typeof GraphBody> & {
    brains: readonly string[];
    hl: number;
    hover: { n: GraphNode; x: number; y: number } | null;
    last: ReadonlyMap<number, LastAccess>;
    stale: ReadonlyMap<number, Staleness>;
  },
) {
  return (
    <div className="relative min-h-0 flex-1 bg-canvas">
      <GraphBody {...p} />
      {/* legend */}
      {p.graph.data ? <GraphLegend brains={p.brains} hl={p.hl} /> : null}
      {p.hover ? <HoverCard hover={p.hover} last={p.last} stale={p.stale} /> : null}
    </div>
  );
}

/** The selected node's recent uses (newest first). */
function RecentUses({ accesses, brains, devDetails }: { accesses: readonly MemoryAccessEvent[]; brains: readonly string[]; devDetails: boolean }) {
  if (!accesses.length) return null;
  return (
    <section>
      <h4 className="mb-1.5 text-[11px] font-semibold tracking-wide text-subtle uppercase">Recent uses</h4>
      <ul className="space-y-1 text-xs">
        {accesses.map((e) => (
          <li key={e.seq} className="flex items-center gap-2">
            <span className="size-2 rounded-full" style={{ background: brainColor(e.brain, brains) }} />
            <span className="text-fg">{e.brain}</span>
            <span className="text-subtle">
              {e.source}
              {devDetails && e.turn ? ` · turn ${e.turn}` : ''}
            </span>
            <span className="ml-auto text-subtle">{ago(e.at)}</span>
          </li>
        ))}
      </ul>
    </section>
  );
}

export interface Hot {
  id: number;
  la: LastAccess;
  h: number;
  n: { label: string; layer: number } | undefined;
}

/** The regions glowing brightest now (or at the replay time); a click selects and reveals one. */
function MostActive({ hottest, brains, at, onPick }: { hottest: readonly Hot[]; brains: readonly string[]; at: number | null; onPick: (id: number) => void }) {
  return (
    <div className="p-5">
      <h2 className="mb-1 flex items-center gap-2 text-sm font-semibold text-fg">
        <Activity className="size-4 text-subtle" /> Most active now
      </h2>
      <p className="mb-4 text-xs text-muted">Regions glowing brightest {at === null ? 'right now' : `at ${clock(at)}`}. Click one, or a node on the map.</p>
      {hottest.length === 0 ? (
        <p className="text-sm text-muted">Nothing recalled in this window.</p>
      ) : (
        <ul className="space-y-1">
          {hottest.map(({ id, la, h, n }) => (
            <li key={id}>
              <button onClick={() => onPick(id)} className="flex w-full items-center gap-3 rounded-lg px-2 py-2 text-left hover:bg-surface-2">
                <span className="size-2.5 shrink-0 rounded-full" style={{ background: brainColor(la.brain, brains), opacity: 0.4 + h * 0.6 }} />
                <span className="min-w-0 flex-1">
                  <span className="block truncate text-[13px] text-fg">{n?.label}</span>
                  <span className="block text-[11px] text-subtle">
                    {la.brain} · {ago(la.at)}
                  </span>
                </span>
                <span className="h-1 w-12 overflow-hidden rounded-full bg-surface-3">
                  <span className="block h-full rounded-full bg-accent" style={{ width: `${h * 100}%` }} />
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

/** The side panel: the selected node's detail and recent uses, or the most active regions. */
export function ActivitySide(p: {
  selected: number | null;
  accesses: readonly MemoryAccessEvent[];
  hottest: readonly Hot[];
  brains: readonly string[];
  devDetails: boolean;
  /** The replay time, or null when live. */
  replayAt: number | null;
  onPick: (id: number) => void;
}): ReactNode {
  return (
    <aside className="min-h-0 overflow-auto border-t border-border bg-surface lg:border-t-0 lg:border-l">
      {p.selected !== null ? (
        <NodeDetail id={p.selected} onNavigate={p.onPick} extra={<RecentUses accesses={p.accesses} brains={p.brains} devDetails={p.devDetails} />} />
      ) : (
        <MostActive hottest={p.hottest} brains={p.brains} at={p.replayAt} onPick={p.onPick} />
      )}
    </aside>
  );
}
