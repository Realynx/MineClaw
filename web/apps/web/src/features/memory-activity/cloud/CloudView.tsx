'use client';

/**
 * Memory activity → Cloud: every memory as a point, coloured by the bot's own RAPTOR cluster and lit by how recently a brain used
 * it (the same heat the map layouts show). The bridge lays the cloud out in several arrangements: Distances (metric MDS, the
 * default: drawn distance ≈ embedding distance, with a fidelity badge and a scale bar), Neighbourhoods (UMAP), PCA and Tree (force
 * along the RAPTOR links). One is shown at a time (switching animates the points) or 2–4 side by side in Compare, with hover,
 * selection, search, lasso, heat and time linked across panes. Legend toggles, search, the link overlay, Shift-drag lasso, hover
 * tooltip (with the embedding distance to the selected memory) and click-to-detail; a calm pulse marks memories as they are
 * recalled live.
 */
import { toNumber, type CloudArrangement, type MemoryAccessEvent, type MemoryCloudResponse } from '@mineclaw/contracts';
import { useEffect, useMemo, useState } from 'react';
import { Badge } from '@/components/ui/primitives';
import { Tooltip } from '@/components/ui/overlays';
import { useApi } from '@/lib/api/query';
import type { ActivityFilter, LastAccess } from '../heat';
import { CloudCanvas, type CloudCanvasHandle, type CloudPane } from './CloudCanvas';
import { cloudBrightness, fidelityBadge, legendEntries, type Theme } from './cloudStyle';
import {
  ClusterLegend,
  CloudToolbar,
  DistanceLine,
  HoverTip,
  LassoPanel,
  PaneTitle,
  useAlignedPanes,
  useHighlight,
  useLivePulses,
} from './CloudViewParts';

export interface CloudViewPane {
  arrangement: CloudArrangement;
  /** The arrangement's last layout (possibly of the previous one while it loads), or null before any. */
  cloud: MemoryCloudResponse | null;
  /** The bridge is still laying this arrangement out (or the request is in flight). */
  pending: boolean;
}

export function FidelityChip({ arrangement, cloud }: { arrangement: CloudArrangement; cloud: MemoryCloudResponse | null }) {
  const b = fidelityBadge(arrangement, cloud?.arrangement === arrangement ? cloud.fidelity : null);
  if (!b) return null;
  return (
    <Tooltip content={b.tooltip} side="bottom">
      <span tabIndex={0} className="inline-flex rounded-md outline-none focus-visible:ring-2 focus-visible:ring-[var(--ring)]">
        <Badge tone={b.tone} data-testid="cloud-fidelity">
          {b.text} <span className="font-normal opacity-80">({b.detail})</span>
        </Badge>
      </span>
    </Tooltip>
  );
}

export function CloudView({
  panes: viewPanes,
  arrangement,
  onArrangement,
  compare,
  onCompare,
  showLinks,
  onShowLinks,
  last,
  t,
  halfLifeS,
  windowMs,
  live,
  events,
  filter,
  theme,
  selected,
  onSelect,
  hits,
  hitQuery,
  onClearHits,
  canvasRef,
}: {
  /** One pane, or 2–4 in Compare; the first is the primary (its points, clusters and titles drive the legend and tooltips). */
  panes: readonly CloudViewPane[];
  arrangement: CloudArrangement;
  onArrangement: (a: CloudArrangement) => void;
  /** The compared arrangements, or null for one pane. */
  compare: readonly CloudArrangement[] | null;
  onCompare: (list: CloudArrangement[] | null) => void;
  showLinks: boolean;
  onShowLinks: (on: boolean) => void;
  last: ReadonlyMap<number, LastAccess>;
  t: number;
  halfLifeS: number;
  windowMs: number;
  /** Not replaying: new accesses pulse. */
  live: boolean;
  events: readonly MemoryAccessEvent[];
  filter: ActivityFilter;
  theme: Theme;
  selected: number | null;
  onSelect: (id: number | null) => void;
  /** Ids lit by a Search page hand-off (`?hits=`). */
  hits: ReadonlySet<number>;
  hitQuery: string | null;
  onClearHits: () => void;
  canvasRef: React.RefObject<CloudCanvasHandle | null>;
}) {
  const cloud = viewPanes[0]!.cloud!;
  const n = cloud.points.id.length;
  const [hidden, setHidden] = useState<Set<number>>(() => new Set());
  const [query, setQuery] = useState('');
  const [lasso, setLasso] = useState<number[]>([]);
  const [hover, setHover] = useState<{ i: number; x: number; y: number; pane: number } | null>(null);
  const indexOf = useMemo(() => new Map(cloud.points.id.map((id, i) => [id, i])), [cloud.points.id]);
  const panes = useAlignedPanes(viewPanes, cloud);

  const brightness = useMemo(() => {
    const b = new Float32Array(n);
    for (let i = 0; i < n; i++) b[i] = cloudBrightness(last.get(cloud.points.id[i]!), t, halfLifeS, windowMs);
    return b;
  }, [n, last, cloud.points.id, t, halfLifeS, windowMs]);
  const lit = useMemo(() => brightness.reduce((s, v) => s + (v > 0.05 ? 1 : 0), 0), [brightness]);
  const highlight = useHighlight(query, hits, cloud);
  const matchCount = useMemo(() => (highlight ? highlight.reduce((s, v) => s + v, 0) : 0), [highlight]);
  const pulses = useLivePulses(events, live, filter, indexOf);
  const legend = useMemo(() => legendEntries(cloud, theme), [cloud, theme]);
  const comparing = compare !== null;

  return (
    <div className="absolute inset-0 flex min-h-0 flex-col" data-lit={lit} data-testid="memory-cloud">
      <CloudToolbar
        query={query}
        setQuery={setQuery}
        matchCount={matchCount}
        hits={hits}
        hitQuery={hitQuery}
        onClearHits={onClearHits}
        arrangement={arrangement}
        onArrangement={onArrangement}
        compare={compare}
        onCompare={onCompare}
        fidelity={<FidelityChip arrangement={arrangement} cloud={viewPanes[0]!.cloud} />}
        layingOut={viewPanes.some((p) => p.pending) || cloud.status === 'computing'}
        showLinks={showLinks}
        onShowLinks={onShowLinks}
        onFit={() => canvasRef.current?.fit()}
      />
      <div className="relative min-h-0 flex-1 bg-canvas">
        <CloudCanvas
          ref={canvasRef}
          cloud={cloud}
          panes={panes}
          brightness={brightness}
          theme={theme}
          hidden={hidden}
          highlight={highlight}
          selectedId={selected}
          showLinks={showLinks}
          pulses={pulses}
          lasso={lasso}
          onHover={(i, x, y, pane) => setHover(i === null ? null : { i, x, y, pane })}
          onSelect={onSelect}
          onLasso={setLasso}
          paneOverlay={comparing ? (i) => (viewPanes[i] ? <PaneTitle p={viewPanes[i]} /> : null) : undefined}
          className="absolute inset-0"
          label="Memory cloud"
        />

        {/* legend: cluster toggles (Alt-click shows one cluster alone) */}
        <ClusterLegend cloud={cloud} legend={legend} hidden={hidden} setHidden={setHidden} hasSelection={selected !== null && indexOf.has(selected)} />

        <CloudHover cloud={cloud} hover={hover} legend={legend} last={last} selected={selected} indexOf={indexOf} panes={panes} />

        <LassoPanel cloud={cloud} lasso={lasso} legend={legend} last={last} onClear={() => setLasso([])} onSelect={onSelect} />
      </div>
    </div>
  );
}

/** The point hovered for a short dwell (so sweeping across points doesn't fetch every one), its text and its distance to the selection. */
function useDwell(
  hover: { i: number } | null,
  cloud: MemoryCloudResponse,
  selected: number | null,
  indexOf: ReadonlyMap<number, number>,
) {
  const [dwell, setDwell] = useState<number | null>(null);
  useEffect(() => {
    const id = hover ? cloud.points.id[hover.i]! : null;
    if (id === null) {
      setDwell(null);
      return;
    }
    const h = setTimeout(() => setDwell(id), 280);
    return () => clearTimeout(h);
  }, [hover, cloud.points.id]);
  const detail = useApi('raptor.node', { params: { id: dwell ?? 0 } }, { enabled: dwell !== null, staleTime: 5 * 60_000 });
  const selectedIx = selected === null ? undefined : indexOf.get(selected);
  const distance = useApi(
    'memoryActivity.cloudDistance',
    { query: { a: selected ?? 0, b: dwell ?? 0 } },
    { enabled: dwell !== null && selectedIx !== undefined && dwell !== selected, staleTime: Infinity },
  );
  return { dwell, detail: detail.data, distance: distance.data, selectedIx };
}

/**
 * The hover tooltip, with the memory's text and its embedding distance to the selected one fetched after a short dwell, so
 * sweeping across points doesn't fetch every one.
 */
function CloudHover(p: {
  cloud: MemoryCloudResponse;
  hover: { i: number; x: number; y: number; pane: number } | null;
  legend: ReturnType<typeof legendEntries>;
  last: ReadonlyMap<number, LastAccess>;
  selected: number | null;
  indexOf: ReadonlyMap<number, number>;
  panes: readonly CloudPane[];
}) {
  const { cloud, hover } = p;
  const { dwell, detail, distance, selectedIx } = useDwell(hover, cloud, p.selected, p.indexOf);
  if (!hover) return null;
  const hoverId = cloud.points.id[hover.i]!;
  const dwelt = dwell === hoverId;
  const embedding = distance && dwelt && distance.b === hoverId ? toNumber(distance.distance) : null;
  return (
    <HoverTip
      cloud={cloud}
      hover={hover}
      legend={p.legend}
      text={detail && dwelt ? detail.text : null}
      last={p.last.get(hoverId)}
      distance={
        selectedIx !== undefined && hover.i !== selectedIx ? (
          <DistanceLine embedding={embedding} pane={p.panes[hover.pane]} selectedIx={selectedIx} hoverIx={hover.i} />
        ) : null
      }
    />
  );
}
