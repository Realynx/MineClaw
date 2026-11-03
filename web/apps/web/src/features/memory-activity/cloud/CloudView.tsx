'use client';

/**
 * Memory activity → Cloud: every memory at its UMAP spot (bridge-computed from the leaf embeddings), coloured by the bot's own
 * RAPTOR cluster and lit by how recently a brain used it (the same heat the map layouts show). Legend toggles, search, a faded
 * link overlay, Shift-drag lasso, hover tooltip and click-to-detail; a calm pulse marks memories as they are recalled live.
 */
import type { MemoryAccessEvent, MemoryCloudResponse } from '@mineclaw/contracts';
import { Loader2, Maximize2, Search, X } from 'lucide-react';
import { useEffect, useMemo, useRef, useState } from 'react';
import { Badge, Button, Input, Kbd } from '@/components/ui/primitives';
import { Switch, Tooltip } from '@/components/ui/overlays';
import { useApi } from '@/lib/api/query';
import { ago } from '@/lib/utils';
import { matches, type ActivityFilter, type LastAccess } from '../heat';
import { CloudCanvas, type CloudCanvasHandle, type CloudPulse } from './CloudCanvas';
import { cloudBrightness, legendEntries, legendKey, matchesQuery, toggleLegend, type Theme } from './cloudStyle';

const PULSE_MS = 1600;
const MAX_PULSES = 24;

export function CloudView({
  cloud,
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
  cloud: MemoryCloudResponse;
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
  const n = cloud.points.id.length;
  const [hidden, setHidden] = useState<Set<number>>(() => new Set());
  const [query, setQuery] = useState('');
  const [showLinks, setShowLinks] = useState(false);
  const [lasso, setLasso] = useState<number[]>([]);
  const [hover, setHover] = useState<{ i: number; x: number; y: number } | null>(null);
  const [dwell, setDwell] = useState<number | null>(null);
  const [pulses, setPulses] = useState<CloudPulse[]>([]);
  const indexOf = useMemo(() => new Map(cloud.points.id.map((id, i) => [id, i])), [cloud.points.id]);

  const brightness = useMemo(() => {
    const b = new Float32Array(n);
    for (let i = 0; i < n; i++) b[i] = cloudBrightness(last.get(cloud.points.id[i]!), t, halfLifeS, windowMs);
    return b;
  }, [n, last, cloud.points.id, t, halfLifeS, windowMs]);
  const lit = useMemo(() => brightness.reduce((s, v) => s + (v > 0.05 ? 1 : 0), 0), [brightness]);

  const highlight = useMemo(() => {
    const q = query.trim();
    if (!q && hits.size === 0) return null;
    const out = new Uint8Array(n);
    for (let i = 0; i < n; i++)
      out[i] = (q ? matchesQuery(cloud.points.title[i]!, cloud.points.id[i]!, q) : hits.has(cloud.points.id[i]!))
        ? 1
        : 0;
    return out;
  }, [query, hits, n, cloud.points.title, cloud.points.id]);
  const matchCount = useMemo(() => (highlight ? highlight.reduce((s, v) => s + v, 0) : 0), [highlight]);

  // Live pulses: one calm ring per memory a brain reads (matching the hive filter), at most MAX_PULSES at once.
  const lastSeq = useRef<number | null>(null);
  useEffect(() => {
    const maxSeq = events.reduce((m, e) => Math.max(m, e.seq), 0);
    if (lastSeq.current === null || !live) {
      lastSeq.current = maxSeq;
      return;
    }
    const now = performance.now();
    const fresh = events
      .filter(
        (e) =>
          e.seq > lastSeq.current! && matches(e, filter) && e.raptorNodeId !== null && e.raptorNodeId !== undefined,
      )
      .map((e) => indexOf.get(e.raptorNodeId!))
      .filter((i): i is number => i !== undefined);
    lastSeq.current = maxSeq;
    if (fresh.length)
      setPulses((cur) =>
        [...cur.filter((p) => now - p.at < PULSE_MS), ...fresh.map((index, k) => ({ index, at: now + k * 90 }))].slice(
          -MAX_PULSES,
        ),
      );
  }, [events, live, filter, indexOf]);

  // Hover detail (the memory's text) after a short dwell, so sweeping across points doesn't fetch every one.
  useEffect(() => {
    const id = hover ? cloud.points.id[hover.i]! : null;
    if (id === null) {
      setDwell(null);
      return;
    }
    const h = setTimeout(() => setDwell(id), 280);
    return () => clearTimeout(h);
  }, [hover, cloud.points.id]);
  const detail = useApi(
    'raptor.node',
    { params: { id: dwell ?? 0 } },
    { enabled: dwell !== null, staleTime: 5 * 60_000 },
  );

  const legend = useMemo(() => legendEntries(cloud, theme), [cloud, theme]);
  const keys = legend.map((l) => l.key);
  const clusterLabel = (c: number) => (c >= 0 ? (cloud.clusters[c]?.label ?? `cluster ${c}`) : 'unclustered');

  const hi = hover?.i;
  const hoverId = hi !== undefined ? cloud.points.id[hi]! : null;
  const hoverLast = hoverId !== null ? last.get(hoverId) : undefined;

  return (
    <div className="absolute inset-0 flex min-h-0 flex-col" data-lit={lit} data-testid="memory-cloud">
      <div className="flex min-h-10 flex-wrap items-center gap-2 border-b border-border bg-surface px-4 py-1.5">
        <div className="relative">
          <Search className="pointer-events-none absolute top-1/2 left-2 size-3.5 -translate-y-1/2 text-subtle" />
          <Input
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="Find memories"
            aria-label="Find memories in the cloud"
            className="h-7 w-52 pl-7 text-xs"
          />
        </div>
        {query.trim() ? (
          <span className="text-xs text-muted" aria-live="polite">
            {matchCount} match{matchCount === 1 ? '' : 'es'}
          </span>
        ) : null}
        {hits.size && !query.trim() ? (
          <Badge tone="warn">
            <Search className="size-3" /> {hits.size} hits{hitQuery ? ` for “${hitQuery}”` : ''}
            <button onClick={onClearHits} aria-label="Clear search hits" className="ml-1">
              <X className="size-3" />
            </button>
          </Badge>
        ) : null}
        <span className="ml-auto flex items-center gap-3">
          {cloud.status === 'computing' ? (
            <Badge tone="info">
              <Loader2 className="size-3 animate-spin" /> updating layout
            </Badge>
          ) : null}
          <Switch
            checked={showLinks}
            onCheckedChange={setShowLinks}
            label={<span className="text-xs text-muted">Links</span>}
          />
          <Tooltip content="Fit (F)">
            <Button size="icon-sm" variant="ghost" aria-label="Fit" onClick={() => canvasRef.current?.fit()}>
              <Maximize2 className="size-3.5" />
            </Button>
          </Tooltip>
        </span>
      </div>
      <div className="relative min-h-0 flex-1 bg-canvas">
        <CloudCanvas
          ref={canvasRef}
          cloud={cloud}
          brightness={brightness}
          theme={theme}
          hidden={hidden}
          highlight={highlight}
          selectedId={selected}
          showLinks={showLinks}
          pulses={pulses}
          lasso={lasso}
          onHover={(i, x, y) => setHover(i === null ? null : { i, x, y })}
          onSelect={onSelect}
          onLasso={setLasso}
          className="absolute inset-0"
          label="Memory cloud"
        />

        {/* legend: cluster toggles (Alt-click shows one cluster alone) */}
        <div className="absolute top-3 left-3 max-w-[17rem] rounded-lg border border-border bg-surface p-2 text-[11px] text-muted shadow-card">
          <div className="mb-1 px-1 font-semibold tracking-wide text-subtle uppercase">
            Clusters (layer {cloud.clusterLayer})
          </div>
          <ul className="space-y-0.5">
            {legend.map((l) => {
              const on = !hidden.has(l.key);
              return (
                <li key={l.key}>
                  <button
                    type="button"
                    aria-pressed={on}
                    title="Click to hide or show; Alt-click to show only this one"
                    onClick={(e) => setHidden((h) => toggleLegend(h, l.key, keys, e.altKey))}
                    className="flex w-full items-center gap-2 rounded px-1 py-0.5 text-left hover:bg-surface-2"
                  >
                    <span
                      className="size-2.5 shrink-0 rounded-full"
                      style={{ background: on ? l.color : 'transparent', boxShadow: `inset 0 0 0 1.5px ${l.color}` }}
                    />
                    <span className={on ? 'min-w-0 flex-1 truncate text-fg' : 'min-w-0 flex-1 truncate line-through'}>
                      {l.label}
                    </span>
                    <span className="tabular text-subtle">{l.size}</span>
                  </button>
                </li>
              );
            })}
          </ul>
          <div className="mt-1.5 border-t border-border px-1 pt-1.5">
            Bright = used recently · dim = not used in this window
          </div>
        </div>

        {hover && hoverId !== null ? (
          <div
            role="tooltip"
            className="pointer-events-none absolute z-10 max-w-80 rounded-lg border border-border bg-surface px-3 py-2 text-xs shadow-pop"
            style={{ left: hover.x + 14, top: hover.y + 14 }}
          >
            <div className="font-medium text-fg">{cloud.points.title[hover.i]}</div>
            {detail.data && dwell === hoverId ? (
              <div className="mt-0.5 line-clamp-3 text-muted">{detail.data.text.slice(0, 220)}</div>
            ) : null}
            <div className="mt-1 flex items-center gap-1.5 text-muted">
              <span
                className="size-2 rounded-full"
                style={{ background: legend.find((l) => l.key === legendKey(cloud.points.cluster[hover.i]!))?.color }}
              />
              {clusterLabel(cloud.points.cluster[hover.i]!)} · {cloud.kinds[cloud.points.kind[hover.i]!]}
            </div>
            <div className="text-muted">
              {hoverLast ? `used ${ago(hoverLast.at)} by ${hoverLast.brain || 'a brain'}` : 'not used in this window'}
            </div>
          </div>
        ) : null}

        {lasso.length ? (
          <section
            aria-label="Lasso selection"
            className="absolute right-3 bottom-3 flex max-h-[45%] w-80 flex-col rounded-lg border border-border bg-surface shadow-card"
          >
            <div className="flex items-center gap-2 border-b border-border px-3 py-2 text-xs">
              <span className="font-medium text-fg">{lasso.length} memories selected</span>
              <button
                onClick={() => setLasso([])}
                className="ml-auto text-muted hover:text-fg"
                aria-label="Clear the lasso selection"
              >
                <X className="size-3.5" />
              </button>
            </div>
            <ul className="min-h-0 flex-1 overflow-auto py-1 text-xs">
              {lasso.slice(0, 300).map((i) => (
                <li key={i}>
                  <button
                    onClick={() => onSelect(cloud.points.id[i]!)}
                    className="flex w-full items-center gap-2 px-3 py-1 text-left hover:bg-surface-2"
                  >
                    <span
                      className="size-2 shrink-0 rounded-full"
                      style={{ background: legend.find((l) => l.key === legendKey(cloud.points.cluster[i]!))?.color }}
                    />
                    <span className="min-w-0 flex-1 truncate text-fg">{cloud.points.title[i]}</span>
                    <span className="text-subtle">
                      {last.get(cloud.points.id[i]!) ? ago(last.get(cloud.points.id[i]!)!.at) : ''}
                    </span>
                  </button>
                </li>
              ))}
              {lasso.length > 300 ? <li className="px-3 py-1 text-subtle">and {lasso.length - 300} more</li> : null}
            </ul>
          </section>
        ) : (
          <div className="pointer-events-none absolute right-3 bottom-3 hidden items-center gap-1.5 rounded-lg border border-border bg-surface px-2.5 py-1.5 text-[11px] text-muted shadow-card xl:flex">
            <Kbd>Shift</Kbd>+drag lasso <Kbd>F</Kbd> fit <Kbd>Esc</Kbd> clear · {n.toLocaleString()} memories
          </div>
        )}
      </div>
    </div>
  );
}
