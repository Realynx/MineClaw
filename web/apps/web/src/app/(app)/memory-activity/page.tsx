'use client';

import { toNumber, type GraphLayout, type GraphNode } from '@mineclaw/contracts';
import { Activity, Pause, Play, Radio } from 'lucide-react';
import { useRouter, useSearchParams } from 'next/navigation';
import { Suspense, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Badge, Button, Card, EmptyState, ErrorState, Select, Skeleton } from '@/components/ui/primitives';
import { Segmented } from '@/components/ui/overlays';
import { GraphCanvas, type GraphCanvasHandle, type NodeStyle } from '@/features/graph/GraphCanvas';
import { indexGraph } from '@/features/graph/geometry';
import {
  brainColor,
  density,
  filterKey,
  halfLife,
  heatAt,
  lastAccessFromEvents,
  lastAccessFromServer,
  mergeLast,
  parseFilter,
  stalenessByNode,
  stalenessColor,
  stalenessShare,
  type ActivityFilter,
} from '@/features/memory-activity/heat';
import { NodeDetail } from '@/features/raptor/NodeDetail';
import { useApi } from '@/lib/api/query';
import { useMemoryActivity } from '@/lib/live/hooks';
import { useTicker } from '@/lib/live/ticker';
import { usePrefs } from '@/lib/prefs';
import { ago, clock, cn, duration } from '@/lib/utils';

const WINDOWS = [5, 15, 30, 60] as const;
const LAYER_GLOW = [1, 0.75, 0.4, 0.22];

function Scrubber({
  from,
  to,
  value,
  onChange,
  counts,
  playing,
  onPlay,
}: {
  from: number;
  to: number;
  value: number | null;
  onChange: (t: number | null) => void;
  counts: number[];
  playing: boolean;
  onPlay: () => void;
}) {
  const max = Math.max(1, ...counts);
  const t = value ?? to;
  const pct = Math.max(0, Math.min(100, ((t - from) / Math.max(1, to - from)) * 100));
  const seek = (e: { clientX: number; currentTarget: EventTarget }) => {
    const r = (e.currentTarget as HTMLElement).getBoundingClientRect();
    const f = Math.max(0, Math.min(1, (e.clientX - r.left) / r.width));
    const v = from + f * (to - from);
    onChange(v >= to - 1500 ? null : v);
  };
  return (
    <div className="flex items-center gap-3 border-t border-border bg-surface px-4 py-3">
      <Button size="icon-sm" variant="subtle" onClick={onPlay} aria-label={playing ? 'Pause replay' : 'Replay the window'}>
        {playing ? <Pause className="size-3.5" /> : <Play className="size-3.5" />}
      </Button>
      <div
        role="slider"
        tabIndex={0}
        aria-label="Replay time"
        aria-valuemin={from}
        aria-valuemax={to}
        aria-valuenow={t}
        aria-valuetext={value === null ? 'live' : clock(t)}
        className="relative h-10 min-w-0 flex-1 cursor-pointer touch-none rounded-md focus-visible:outline-2"
        onPointerDown={(e) => {
          (e.currentTarget as HTMLDivElement).setPointerCapture(e.pointerId);
          seek(e);
        }}
        onPointerMove={(e) => {
          if (e.buttons) seek(e);
        }}
        onKeyDown={(e) => {
          const step = (to - from) / 60;
          if (e.key === 'ArrowLeft') onChange(Math.max(from, t - step));
          else if (e.key === 'ArrowRight') onChange(t + step >= to - 1500 ? null : t + step);
          else if (e.key === 'End') onChange(null);
          else if (e.key === 'Home') onChange(from);
          else return;
          e.preventDefault();
        }}
      >
        <svg className="absolute inset-0 size-full" viewBox={`0 0 ${counts.length} 100`} preserveAspectRatio="none" aria-hidden>
          {counts.map((c, i) => {
            const h = 8 + (c / max) * 88;
            return (
              <rect
                key={i}
                x={i + 0.12}
                y={100 - h}
                width={0.76}
                height={h}
                rx={0.2}
                className={(i + 0.5) / counts.length <= pct / 100 ? 'fill-accent/70' : 'fill-surface-3'}
              />
            );
          })}
        </svg>
        <div className="absolute inset-y-0 w-0.5 -translate-x-1/2 rounded-full bg-fg" style={{ left: `${pct}%` }} aria-hidden />
      </div>
      <div className="w-28 shrink-0 text-right">
        {value === null ? (
          <Badge tone="ok">
            <Radio className="size-3" /> live
          </Badge>
        ) : (
          <button onClick={() => onChange(null)} className="text-xs text-muted hover:text-fg">
            <span className="tabular font-medium text-fg">{clock(t)}</span> · back to live
          </button>
        )}
      </div>
    </div>
  );
}

function ActivityInner() {
  const params = useSearchParams();
  const router = useRouter();
  const { devDetails } = usePrefs();
  const filter = parseFilter(params.get('who'));
  const layout = (params.get('layout') as GraphLayout | null) ?? 'radial';
  const windowMin = Number(params.get('window') ?? 15) as (typeof WINDOWS)[number];
  const setParam = (k: string, v: string | null) => {
    const sp = new URLSearchParams(params.toString());
    if (v === null) sp.delete(k);
    else sp.set(k, v);
    router.replace(`/memory-activity?${sp.toString()}`, { scroll: false });
  };

  const graph = useApi('raptor.graph', { query: { layout, colorBy: 'kind' } });
  const heat = useApi(
    'memoryActivity.heat',
    { query: { windowS: windowMin * 60, ...(filter.kind === 'brain' ? { brain: filter.brain } : {}) } },
    { refetchInterval: 30_000 },
  );
  const activity = useMemoryActivity();
  const [replayT, setReplayT] = useState<number | null>(null);
  const [playing, setPlaying] = useState(false);
  const [selected, setSelected] = useState<number | null>(null);
  const [hover, setHover] = useState<{ n: GraphNode; x: number; y: number } | null>(null);
  const canvasRef = useRef<GraphCanvasHandle>(null);
  const now = useTicker(1000);

  const idx = useMemo(() => (graph.data ? indexGraph(graph.data) : null), [graph.data]);
  const events = useMemo(() => activity.data?.events ?? [], [activity.data]);
  const brains = useMemo(() => [...new Set([...(heat.data?.brains ?? []), ...events.map((e) => e.brain)])].sort(), [heat.data, events]);
  const bodies = useMemo(() => [...new Set(events.map((e) => e.body).filter((b): b is string => !!b))].sort(), [events]);
  const bodyBrain = filter.kind === 'body' ? events.find((e) => e.body === filter.body)?.brain : undefined;
  const hl = halfLife(heat.data);
  const from = now - windowMin * 60_000;
  const t = replayT ?? now;

  // Replay: advance 30× real time; stop at the live edge.
  useEffect(() => {
    if (!playing) return;
    const id = setInterval(() => {
      setReplayT((cur) => {
        const next = (cur ?? from) + 30_000 / 4;
        if (next >= Date.now() - 1000) {
          setPlaying(false);
          return null;
        }
        return next;
      });
    }, 250);
    return () => clearInterval(id);
  }, [playing, from]);

  const last = useMemo(() => {
    if (!idx) return new Map();
    const fromEvents = lastAccessFromEvents(events, idx.parents, t, filter);
    return replayT === null ? mergeLast(lastAccessFromServer(heat.data, filter, bodyBrain), fromEvents) : fromEvents;
    // eslint-disable-next-line react-hooks/exhaustive-deps -- filterKey captures the filter
  }, [idx, events, t, replayT, heat.data, filterKey(filter), bodyBrain]);
  const stale = useMemo(
    () => (replayT === null ? stalenessByNode(activity.data?.inContext ?? [], now, filter, bodyBrain) : new Map()),
    // eslint-disable-next-line react-hooks/exhaustive-deps -- filterKey captures the filter
    [activity.data, now, replayT, filterKey(filter), bodyBrain],
  );

  const styleFor = useCallback(
    (n: GraphNode): NodeStyle => {
      const la = last.get(n.id);
      const h = heatAt(la, t, hl);
      const s = stale.get(n.id);
      return {
        fill: n.color,
        alpha: 0.14 + toNumber(n.opacity) * 0.12,
        // Ancestors carry the propagated heat; damp it by layer so a region glows without washing out the centre.
        glow: h > 0.02 && la ? { color: brainColor(la.brain, brains), strength: h * (LAYER_GLOW[n.layer] ?? 0.2) } : null,
        ring: s ? { color: s.evicted ? '#8a929c' : stalenessColor(s.stalenessS), width: 0.4 + stalenessShare(s.stalenessS) * 0.6, hollow: s.evicted } : null,
      };
    },
    [last, t, hl, stale, brains],
  );

  const counts = useMemo(() => density(events, from, now, 60, filter), [events, from, now, filter]);
  const hottest = useMemo(
    () =>
      [...last.entries()]
        .map(([id, la]) => ({ id, la, h: heatAt(la, t, hl), n: idx?.byId.get(id) }))
        .filter((x) => x.n && x.n.layer <= 1 && x.h > 0.05)
        .sort((a, b) => b.h - a.h)
        .slice(0, 8),
    [last, t, hl, idx],
  );
  const selectedAccesses = useMemo(
    () => (selected === null ? [] : events.filter((e) => e.raptorNodeId === selected).slice(-8).reverse()),
    [events, selected],
  );
  const inCtxCount = (activity.data?.inContext ?? []).filter((c) => c.positionFromEnd !== null && c.positionFromEnd !== undefined).length;

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex flex-wrap items-center gap-3 border-b border-border bg-surface px-4 py-3 sm:px-6">
        <div className="mr-auto min-w-0">
          <h1 className="text-lg font-semibold tracking-tight text-fg">Memory activity</h1>
          <p className="text-xs text-muted">
            Lit by what each brain recalls. {inCtxCount} memories in context now.
          </p>
        </div>
        <label className="sr-only" htmlFor="who">
          Whose activity
        </label>
        <Select id="who" value={filterKey(filter)} onChange={(e) => setParam('who', e.target.value === 'all' ? null : e.target.value)} className="h-8 w-auto min-w-44">
          <option value="all">All brains</option>
          <optgroup label="Hive brains">
            {brains.map((b) => (
              <option key={b} value={filterKey({ kind: 'brain', brain: b } satisfies ActivityFilter)}>
                {b}
              </option>
            ))}
          </optgroup>
          <optgroup label="Bodies">
            {bodies.map((b) => (
              <option key={b} value={filterKey({ kind: 'body', body: b } satisfies ActivityFilter)}>
                {b}
              </option>
            ))}
          </optgroup>
        </Select>
        <Select aria-label="Window" value={windowMin} onChange={(e) => setParam('window', e.target.value)} className="h-8 w-auto">
          {WINDOWS.map((w) => (
            <option key={w} value={w}>
              Last {w} min
            </option>
          ))}
        </Select>
        <Segmented<GraphLayout>
          label="Layout"
          size="sm"
          value={layout}
          onChange={(v) => setParam('layout', v)}
          options={[
            { value: 'radial', label: 'Radial' },
            { value: 'icicle', label: 'Icicle' },
            { value: 'nodelink', label: 'Node-link' },
          ]}
        />
      </div>

      <div className="grid min-h-0 flex-1 grid-cols-1 lg:grid-cols-[minmax(0,1fr)_22rem]">
        <div className="relative flex min-h-[28rem] flex-col">
          <div className="relative min-h-0 flex-1 bg-canvas">
            {graph.isPending ? (
              <Skeleton className="absolute inset-6 rounded-xl" />
            ) : graph.error ? (
              <ErrorState error={graph.error} onRetry={() => void graph.refetch()} className="absolute inset-0" />
            ) : graph.data.nodes.length === 0 ? (
              <EmptyState title="No RAPTOR tree yet" className="absolute inset-0" />
            ) : (
              <GraphCanvas
                ref={canvasRef}
                graph={graph.data}
                styleFor={styleFor}
                selected={selected}
                onSelect={setSelected}
                onHover={(n, x, y) => setHover(n ? { n, x, y } : null)}
                frameKey={t}
                label="Memory activity map"
                className="absolute inset-0"
              />
            )}
            {/* legend */}
            {graph.data ? (
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
            ) : null}
            {hover ? (
              <div
                className="pointer-events-none absolute z-10 max-w-72 rounded-lg border border-border bg-surface px-3 py-2 text-xs shadow-pop"
                style={{ left: Math.min(hover.x + 14, 9999), top: hover.y + 14 }}
              >
                <div className="font-medium text-fg">{hover.n.label}</div>
                <div className="text-muted">
                  {hover.n.isLeaf ? 'memory' : `L${hover.n.layer} · ${hover.n.weight} memories`}
                  {last.get(hover.n.id) ? ` · used ${ago(last.get(hover.n.id)!.at)} by ${last.get(hover.n.id)!.brain}` : ' · not used in this window'}
                </div>
                {stale.get(hover.n.id) ? (
                  <div className="text-muted">
                    {stale.get(hover.n.id)!.evicted ? 'evicted from context' : `in context, refreshed ${duration(stale.get(hover.n.id)!.stalenessS)} ago`}
                  </div>
                ) : null}
              </div>
            ) : null}
          </div>
          <Scrubber
            from={from}
            to={now}
            value={replayT}
            onChange={(v) => {
              setPlaying(false);
              setReplayT(v);
            }}
            counts={counts}
            playing={playing}
            onPlay={() => {
              if (playing) setPlaying(false);
              else {
                setReplayT(from);
                setPlaying(true);
              }
            }}
          />
        </div>
        <aside className="min-h-0 overflow-auto border-t border-border bg-surface lg:border-t-0 lg:border-l">
          {selected !== null ? (
            <NodeDetail
              id={selected}
              onNavigate={(id) => {
                setSelected(id);
                canvasRef.current?.reveal(id);
              }}
              extra={
                selectedAccesses.length ? (
                  <section>
                    <h4 className="mb-1.5 text-[11px] font-semibold tracking-wide text-subtle uppercase">Recent uses</h4>
                    <ul className="space-y-1 text-xs">
                      {selectedAccesses.map((e) => (
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
                ) : null
              }
            />
          ) : (
            <div className="p-5">
              <h2 className="mb-1 flex items-center gap-2 text-sm font-semibold text-fg">
                <Activity className="size-4 text-subtle" /> Most active now
              </h2>
              <p className="mb-4 text-xs text-muted">Regions glowing brightest {replayT === null ? 'right now' : `at ${clock(t)}`}. Click one, or a node on the map.</p>
              {hottest.length === 0 ? (
                <p className="text-sm text-muted">Nothing recalled in this window.</p>
              ) : (
                <ul className="space-y-1">
                  {hottest.map(({ id, la, h, n }) => (
                    <li key={id}>
                      <button
                        onClick={() => {
                          setSelected(id);
                          canvasRef.current?.reveal(id);
                        }}
                        className="flex w-full items-center gap-3 rounded-lg px-2 py-2 text-left hover:bg-surface-2"
                      >
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
          )}
        </aside>
      </div>
    </div>
  );
}

export default function MemoryActivityPage() {
  return (
    <Suspense fallback={null}>
      <ActivityInner />
    </Suspense>
  );
}
