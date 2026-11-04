'use client';

/**
 * The memory cloud view's parts (CloudView.tsx holds the state): the toolbar (find, hits, arrangement or comparison, links,
 * fit), a compared pane's title, the cluster legend, the hover tooltip and the lasso list; and the hooks that derive the panes,
 * the search highlight and the live pulses.
 */
import { toNumber, type CloudArrangement, type MemoryAccessEvent, type MemoryCloudResponse } from '@mineclaw/contracts';
import { Columns2, Loader2, Maximize2, Search, X } from 'lucide-react';
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Badge, Button, Input, Kbd } from '@/components/ui/primitives';
import { Segmented, Switch, Tooltip } from '@/components/ui/overlays';
import { ago, cn } from '@/lib/utils';
import { matches, type ActivityFilter, type LastAccess } from '../heat';
import type { CloudPane, CloudPulse } from './CloudCanvas';
import { PULSE_MS } from './cloudDraw';
import {
  ARRANGEMENTS,
  alignXy,
  arrangementMeta,
  drawnDistance,
  fidelityBadge,
  legendKey,
  matchesQuery,
  toggleLegend,
  type legendEntries,
} from './cloudStyle';
import type { CloudViewPane } from './CloudView';

const MAX_PULSES = 24;

type Cloud = MemoryCloudResponse;
type LegendEntry = ReturnType<typeof legendEntries>[number];

/**
 * Every pane in the primary's point order (the bridge keeps one order per tree state). Kept by identity while the pane clouds
 * are the same objects, so the canvas re-uploads (and animates) only when an arrangement really changes.
 */
export function useAlignedPanes(viewPanes: readonly CloudViewPane[], cloud: Cloud): CloudPane[] {
  const paneKey = viewPanes.map((p) => (p.cloud ? `${p.cloud.arrangement}:${p.cloud.version}` : '-')).join('|');
  return useMemo(
    () => viewPanes.map((p) => alignedPane(p.cloud ?? cloud, cloud)),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [paneKey, cloud],
  );
}

function alignedPane(c: Cloud, primary: Cloud): CloudPane {
  const sameSummaries =
    c.summaries.id.length === primary.summaries.id.length && c.summaries.id.every((id, i) => id === primary.summaries.id[i]);
  return {
    key: c.arrangement,
    xy: alignXy(primary.points.id, c.points.id, c.points.xy),
    sxy: sameSummaries ? c.summaries.xy : alignXy(primary.summaries.id, c.summaries.id, c.summaries.xy),
    clusters: c.clusters,
    metric: arrangementMeta(c.arrangement).metric,
    unitDistance: c.fidelity ? toNumber(c.fidelity.unitDistance) : null,
  };
}

/** Per point, 1 when it matches the find box (or, with the box empty, is a search hit); null when neither is in use. */
export function useHighlight(query: string, hits: ReadonlySet<number>, cloud: Cloud): Uint8Array | null {
  const n = cloud.points.id.length;
  return useMemo(() => {
    const q = query.trim();
    if (!q && hits.size === 0) return null;
    const lit = q ? (i: number) => matchesQuery(cloud.points.title[i]!, cloud.points.id[i]!, q) : (i: number) => hits.has(cloud.points.id[i]!);
    const out = new Uint8Array(n);
    for (let i = 0; i < n; i++) out[i] = lit(i) ? 1 : 0;
    return out;
  }, [query, hits, n, cloud.points.title, cloud.points.id]);
}

/** Live pulses: one calm ring per memory a brain reads (matching the hive filter), at most MAX_PULSES at once. */
export function useLivePulses(
  events: readonly MemoryAccessEvent[],
  live: boolean,
  filter: ActivityFilter,
  indexOf: ReadonlyMap<number, number>,
): CloudPulse[] {
  const [pulses, setPulses] = useState<CloudPulse[]>([]);
  const lastSeq = useRef<number | null>(null);
  useEffect(() => {
    const maxSeq = events.reduce((m, e) => Math.max(m, e.seq), 0);
    if (lastSeq.current === null || !live) {
      lastSeq.current = maxSeq;
      return;
    }
    const now = performance.now();
    const fresh = freshIndexes(events, lastSeq.current, filter, indexOf);
    lastSeq.current = maxSeq;
    if (fresh.length)
      setPulses((cur) =>
        [...cur.filter((p) => now - p.at < PULSE_MS), ...fresh.map((index, k) => ({ index, at: now + k * 90 }))].slice(-MAX_PULSES),
      );
  }, [events, live, filter, indexOf]);
  return pulses;
}

/** The points of the events after `seq` that match the filter and name a node in the cloud. */
function freshIndexes(events: readonly MemoryAccessEvent[], seq: number, filter: ActivityFilter, indexOf: ReadonlyMap<number, number>): number[] {
  return events
    .filter((e) => e.seq > seq && matches(e, filter) && e.raptorNodeId !== null && e.raptorNodeId !== undefined)
    .map((e) => indexOf.get(e.raptorNodeId!))
    .filter((i): i is number => i !== undefined);
}

/** The find box, the search-hit badge, the arrangement (or comparison) picker, links and fit. */
export function CloudToolbar(p: {
  query: string;
  setQuery: (q: string) => void;
  matchCount: number;
  hits: ReadonlySet<number>;
  hitQuery: string | null;
  onClearHits: () => void;
  arrangement: CloudArrangement;
  onArrangement: (a: CloudArrangement) => void;
  compare: readonly CloudArrangement[] | null;
  onCompare: (list: CloudArrangement[] | null) => void;
  fidelity: ReactNode;
  layingOut: boolean;
  showLinks: boolean;
  onShowLinks: (on: boolean) => void;
  onFit: () => void;
}) {
  const { compare, arrangement } = p;
  const comparing = compare !== null;
  return (
    <div className="flex min-h-10 flex-wrap items-center gap-2 border-b border-border bg-surface px-4 py-1.5">
      <div className="relative">
        <Search className="pointer-events-none absolute top-1/2 left-2 size-3.5 -translate-y-1/2 text-subtle" />
        <Input
          value={p.query}
          onChange={(e) => p.setQuery(e.target.value)}
          placeholder="Find memories"
          aria-label="Find memories in the cloud"
          className="h-7 w-44 pl-7 text-xs"
        />
      </div>
      <FindStatus query={p.query} matchCount={p.matchCount} hits={p.hits} hitQuery={p.hitQuery} onClearHits={p.onClearHits} />
      {comparing ? (
        <CompareToggles compare={compare} onCompare={p.onCompare} />
      ) : (
        <Segmented<CloudArrangement>
          label="Arrangement"
          size="sm"
          value={arrangement}
          onChange={p.onArrangement}
          options={ARRANGEMENTS.map((a) => ({ value: a.id, label: a.short, title: `${a.label}: ${a.hint}` }))}
        />
      )}
      {comparing ? null : p.fidelity}
      <span className="ml-auto flex items-center gap-3">
        {p.layingOut ? (
          <Badge tone="info">
            <Loader2 className="size-3 animate-spin" /> laying out
          </Badge>
        ) : null}
        <Button
          size="sm"
          variant={comparing ? 'subtle' : 'ghost'}
          aria-pressed={comparing}
          onClick={() => p.onCompare(comparing ? null : [arrangement, arrangement === 'umap' ? 'mds' : 'umap'])}
          title="Show 2–4 arrangements side by side, linked"
        >
          <Columns2 className="size-3.5" /> Compare
        </Button>
        <Switch checked={p.showLinks} onCheckedChange={p.onShowLinks} label={<span className="text-xs text-muted">Links</span>} />
        <Tooltip content="Fit (F)">
          <Button size="icon-sm" variant="ghost" aria-label="Fit" onClick={p.onFit}>
            <Maximize2 className="size-3.5" />
          </Button>
        </Tooltip>
      </span>
    </div>
  );
}

/** How many memories the find box matches, or (box empty) the search hand-off's hits with a way to clear them. */
function FindStatus(p: { query: string; matchCount: number; hits: ReadonlySet<number>; hitQuery: string | null; onClearHits: () => void }) {
  const q = p.query.trim();
  return (
    <>
      {q ? (
        <span className="text-xs text-muted" aria-live="polite">
          {p.matchCount} match{p.matchCount === 1 ? '' : 'es'}
        </span>
      ) : null}
      {p.hits.size && !q ? (
        <Badge tone="warn">
          <Search className="size-3" /> {p.hits.size} hits{p.hitQuery ? ` for “${p.hitQuery}”` : ''}
          <button onClick={p.onClearHits} aria-label="Clear search hits" className="ml-1">
            <X className="size-3" />
          </button>
        </Badge>
      ) : null}
    </>
  );
}

/** Compare mode: which arrangements are side by side (2–4; a toggle that would leave fewer than 2 or more than 4 does nothing). */
function CompareToggles({ compare, onCompare }: { compare: readonly CloudArrangement[]; onCompare: (list: CloudArrangement[] | null) => void }) {
  const toggle = (a: CloudArrangement) => {
    if (compare.includes(a)) {
      if (compare.length > 2) onCompare(compare.filter((x) => x !== a));
    } else if (compare.length < 4) onCompare([...compare, a]);
  };
  return (
    <div role="group" aria-label="Arrangements to compare" className="flex items-center gap-1">
      {ARRANGEMENTS.map((a) => {
        const on = compare.includes(a.id);
        return (
          <button
            key={a.id}
            type="button"
            aria-pressed={on}
            title={a.hint}
            onClick={() => toggle(a.id)}
            className={cn(
              'h-6 rounded-md border px-2 text-xs font-medium transition-colors',
              on ? 'border-accent/50 bg-accent/15 text-fg' : 'border-border text-muted hover:text-fg',
            )}
          >
            {a.short}
          </button>
        );
      })}
    </div>
  );
}

/** A compared pane's title: its arrangement, a spinner while it lays out, its fidelity. */
export function PaneTitle({ p }: { p: CloudViewPane }) {
  const meta = arrangementMeta(p.arrangement);
  const b = fidelityBadge(p.arrangement, p.cloud?.arrangement === p.arrangement ? p.cloud.fidelity : null);
  return (
    <div
      className="m-2 inline-flex items-center gap-1.5 rounded-md border border-border bg-surface/90 px-2 py-1 text-[11px] shadow-card"
      data-testid="cloud-pane-title"
    >
      <span className="font-medium text-fg">{meta.label}</span>
      {p.pending ? <Loader2 className="size-3 animate-spin text-muted" /> : null}
      {b ? <span className="text-muted">{b.detail}</span> : null}
    </div>
  );
}

/** The cluster legend: toggles (Alt-click shows one cluster alone), and how to read the brightness. */
export function ClusterLegend(p: {
  cloud: Cloud;
  legend: readonly LegendEntry[];
  hidden: ReadonlySet<number>;
  setHidden: (f: (h: Set<number>) => Set<number>) => void;
  hasSelection: boolean;
}) {
  const keys = p.legend.map((l) => l.key);
  return (
    <div className="absolute top-3 left-3 max-w-[17rem] rounded-lg border border-border bg-surface p-2 text-[11px] text-muted shadow-card">
      <div className="mb-1 px-1 font-semibold tracking-wide text-subtle uppercase">Clusters (layer {p.cloud.clusterLayer})</div>
      <ul className="space-y-0.5">
        {p.legend.map((l) => {
          const on = !p.hidden.has(l.key);
          return (
            <li key={l.key}>
              <button
                type="button"
                aria-pressed={on}
                title="Click to hide or show; Alt-click to show only this one"
                onClick={(e) => p.setHidden((h) => toggleLegend(h, l.key, keys, e.altKey))}
                className="flex w-full items-center gap-2 rounded px-1 py-0.5 text-left hover:bg-surface-2"
              >
                <span
                  className="size-2.5 shrink-0 rounded-full"
                  style={{ background: on ? l.color : 'transparent', boxShadow: `inset 0 0 0 1.5px ${l.color}` }}
                />
                <span className={on ? 'min-w-0 flex-1 truncate text-fg' : 'min-w-0 flex-1 truncate line-through'}>{l.label}</span>
                <span className="tabular text-subtle">{l.size}</span>
              </button>
            </li>
          );
        })}
      </ul>
      <div className="mt-1.5 border-t border-border px-1 pt-1.5">
        Bright = used recently · dim = not used in this window
        {p.hasSelection ? <div>Hover another memory for its embedding distance</div> : null}
      </div>
    </div>
  );
}

const colorOf = (legend: readonly LegendEntry[], cloud: Cloud, i: number) => legend.find((l) => l.key === legendKey(cloud.points.cluster[i]!))?.color;

/** The hovered memory: title, text (after the dwell), cluster and kind, last use, and its distance to the selected one. */
export function HoverTip(p: {
  cloud: Cloud;
  hover: { i: number; x: number; y: number };
  legend: readonly LegendEntry[];
  /** The memory's text once fetched for this point, else null. */
  text: string | null;
  last: LastAccess | undefined;
  distance: ReactNode;
}) {
  const { cloud, hover } = p;
  const c = cloud.points.cluster[hover.i]!;
  const clusterLabel = c >= 0 ? (cloud.clusters[c]?.label ?? `cluster ${c}`) : 'unclustered';
  return (
    <div
      role="tooltip"
      className="pointer-events-none absolute z-10 max-w-80 rounded-lg border border-border bg-surface px-3 py-2 text-xs shadow-pop"
      style={{ left: hover.x + 14, top: hover.y + 14 }}
    >
      <div className="font-medium text-fg">{cloud.points.title[hover.i]}</div>
      {p.text !== null ? <div className="mt-0.5 line-clamp-3 text-muted">{p.text.slice(0, 220)}</div> : null}
      <div className="mt-1 flex items-center gap-1.5 text-muted">
        <span className="size-2 rounded-full" style={{ background: colorOf(p.legend, cloud, hover.i) }} />
        {clusterLabel} · {cloud.kinds[cloud.points.kind[hover.i]!]}
      </div>
      <div className="text-muted">{p.last ? `used ${ago(p.last.at)} by ${p.last.brain || 'a brain'}` : 'not used in this window'}</div>
      {p.distance}
    </div>
  );
}

/** The hovered memory's distance to the selected one: in the embedding (once fetched) and as drawn (metric arrangements). */
export function DistanceLine(p: {
  embedding: number | null;
  pane: CloudPane | undefined;
  selectedIx: number;
  hoverIx: number;
}) {
  const pane = p.pane;
  return (
    <div className="mt-1 border-t border-border pt-1 text-muted" data-testid="cloud-distance">
      To the selected memory:{' '}
      {p.embedding !== null ? (
        <>
          embedding distance <span className="tabular font-medium text-fg">{p.embedding.toFixed(2)}</span>
        </>
      ) : (
        'embedding distance …'
      )}
      {pane?.metric && pane.unitDistance ? (
        <>
          {' '}
          · drawn{' '}
          <span className="tabular text-fg">{drawnDistance(pane.xy, p.selectedIx, p.hoverIx, pane.unitDistance).toFixed(2)}</span>
        </>
      ) : (
        ' · this arrangement does not draw distances'
      )}
    </div>
  );
}

/** The lasso's memories (up to 300; a click selects one), or the canvas's key hints when there is no lasso. */
export function LassoPanel(p: {
  cloud: Cloud;
  lasso: readonly number[];
  legend: readonly LegendEntry[];
  last: ReadonlyMap<number, LastAccess>;
  onClear: () => void;
  onSelect: (id: number) => void;
}) {
  const { cloud, lasso } = p;
  if (!lasso.length)
    return (
      <div className="pointer-events-none absolute right-3 bottom-3 hidden items-center gap-1.5 rounded-lg border border-border bg-surface px-2.5 py-1.5 text-[11px] text-muted shadow-card xl:flex">
        <Kbd>Shift</Kbd>+drag lasso <Kbd>F</Kbd> fit <Kbd>Esc</Kbd> clear · {cloud.points.id.length.toLocaleString()} memories
      </div>
    );
  return (
    <section aria-label="Lasso selection" className="absolute right-3 bottom-3 flex max-h-[45%] w-80 flex-col rounded-lg border border-border bg-surface shadow-card">
      <div className="flex items-center gap-2 border-b border-border px-3 py-2 text-xs">
        <span className="font-medium text-fg">{lasso.length} memories selected</span>
        <button onClick={p.onClear} className="ml-auto text-muted hover:text-fg" aria-label="Clear the lasso selection">
          <X className="size-3.5" />
        </button>
      </div>
      <ul className="min-h-0 flex-1 overflow-auto py-1 text-xs">
        {lasso.slice(0, 300).map((i) => {
          const used = p.last.get(cloud.points.id[i]!);
          return (
            <li key={i}>
              <button onClick={() => p.onSelect(cloud.points.id[i]!)} className="flex w-full items-center gap-2 px-3 py-1 text-left hover:bg-surface-2">
                <span className="size-2 shrink-0 rounded-full" style={{ background: colorOf(p.legend, cloud, i) }} />
                <span className="min-w-0 flex-1 truncate text-fg">{cloud.points.title[i]}</span>
                <span className="text-subtle">{used ? ago(used.at) : ''}</span>
              </button>
            </li>
          );
        })}
        {lasso.length > 300 ? <li className="px-3 py-1 text-subtle">and {lasso.length - 300} more</li> : null}
      </ul>
    </section>
  );
}
