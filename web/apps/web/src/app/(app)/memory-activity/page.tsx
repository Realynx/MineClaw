'use client';

import { toNumber, type GraphNode, type MemoryActivityResponse } from '@mineclaw/contracts';
import { Pause, Play, Radio } from 'lucide-react';
import { Suspense, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Badge, Button } from '@/components/ui/primitives';
import { ActivitySide, ActivityToolbar, CloudArea, GraphArea, type Hot } from '@/features/memory-activity/ActivityPanels';
import { bodyBrainOf, useActivityParams, useCloudIndex, useCloudPanes, useShowLinks, useWho } from '@/features/memory-activity/activityState';
import type { CloudCanvasHandle } from '@/features/memory-activity/cloud/CloudCanvas';
import type { GraphCanvasHandle, NodeStyle } from '@/features/graph/GraphCanvas';
import { indexGraph } from '@/features/graph/geometry';
import {
  brainColor,
  density,
  halfLife,
  heatAt,
  lastAccessFromEvents,
  lastAccessFromServer,
  mergeLast,
  stalenessByNode,
  stalenessColor,
  stalenessShare,
  type LastAccess,
  type Staleness,
} from '@/features/memory-activity/heat';
import { useApi } from '@/lib/api/query';
import { useMemoryActivity } from '@/lib/live/hooks';
import { useTicker } from '@/lib/live/ticker';
import { resolvedTheme, usePrefs } from '@/lib/prefs';
import { clock } from '@/lib/utils';

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
          (e.currentTarget).setPointerCapture(e.pointerId);
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
                className={(i + 0.5) / counts.length <= pct / 100 ? 'fill-accent/40' : 'fill-surface-3'}
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
  const { devDetails, theme: themePref } = usePrefs();
  const theme = typeof window === 'undefined' ? 'dark' : resolvedTheme(themePref);
  const { params, filter, layout, selected, hits, windowMin, setParams, setParam, setSelected } = useActivityParams();
  const isCloud = layout === 'cloud';
  const graph = useApi('raptor.graph', { query: { layout: isCloud ? 'radial' : layout, colorBy: 'kind' } }, { enabled: !isCloud });
  // Cloud arrangement(s): `arr` (one pane, default the distance layout) or `cmp` (2–4 side by side); links on unless `links=0`
  // (or turned off before, remembered in this browser).
  const { showLinks, setShowLinks } = useShowLinks(params.get('links'), setParam);
  const { arrangement, compare, viewPanes, cloud, primaryCloud } = useCloudPanes(params, isCloud);
  const heat = useApi(
    'memoryActivity.heat',
    { query: { windowS: windowMin * 60, ...(filter.kind === 'brain' ? { brain: filter.brain } : {}) } },
    { refetchInterval: 30_000 },
  );
  const activity = useMemoryActivity();
  const [replayT, setReplayT] = useState<number | null>(null);
  const [playing, setPlaying] = useState(false);
  const [hover, setHover] = useState<{ n: GraphNode; x: number; y: number } | null>(null);
  const canvasRef = useRef<GraphCanvasHandle>(null);
  const cloudRef = useRef<CloudCanvasHandle>(null);
  const reveal = (id: number) => (isCloud ? cloudRef.current?.reveal(id) : canvasRef.current?.reveal(id));
  const now = useTicker(1000);

  const idx = useMemo(() => (graph.data && !isCloud ? indexGraph(graph.data) : null), [graph.data, isCloud]);
  const cloudIdx = useCloudIndex(primaryCloud, isCloud);
  const parents = (isCloud ? cloudIdx : idx)?.parents;
  const events = useMemo(() => activity.data?.events ?? [], [activity.data]);
  const { brains, bodies } = useWho(heat.data?.brains, events);
  const bodyBrain = bodyBrainOf(filter, events);
  const hl = halfLife(heat.data);
  const from = now - windowMin * 60_000;
  const t = replayT ?? now;
  useReplay(playing, from, setReplayT, setPlaying);

  const last = useMemo(() => {
    if (!parents) return new Map<number, LastAccess>();
    const fromEvents = lastAccessFromEvents(events, parents, t, filter);
    return replayT === null ? mergeLast(lastAccessFromServer(heat.data, filter, bodyBrain), fromEvents) : fromEvents;
  }, [parents, events, t, replayT, heat.data, filter, bodyBrain]);
  const stale = useMemo(
    () => (replayT === null ? stalenessByNode(activity.data?.inContext ?? [], now, filter, bodyBrain) : new Map<number, Staleness>()),
    [activity.data, now, replayT, filter, bodyBrain],
  );

  const styleFor = useCallback((n: GraphNode): NodeStyle => nodeStyle(n, last.get(n.id), stale.get(n.id), t, hl, brains), [last, t, hl, stale, brains]);

  const counts = useMemo(() => density(events, from, now, 60, filter), [events, from, now, filter]);
  const hottest = useMemo(
    () =>
      [...last.entries()]
        .map(([id, la]): Hot => ({ id, la, h: heatAt(la, t, hl), n: isCloud ? cloudIdx?.info.get(id) : graphInfo(idx, id) }))
        .filter((x) => x.n && x.n.layer <= (isCloud ? 0 : 1) && x.h > 0.05)
        .sort((a, b) => b.h - a.h)
        .slice(0, 8),
    [last, t, hl, idx, cloudIdx, isCloud],
  );
  const selectedAccesses = useMemo(
    () => (selected === null ? [] : events.filter((e) => e.raptorNodeId === selected).slice(-8).reverse()),
    [events, selected],
  );
  const inCtxCount = inContextCount(activity.data);
  const pick = (id: number) => {
    setSelected(id);
    reveal(id);
  };

  return (
    <div className="flex h-full min-h-0 flex-col">
      <ActivityToolbar filter={filter} brains={brains} bodies={bodies} windowMin={windowMin} layout={layout} inCtxCount={inCtxCount} setParam={setParam} />

      <div className="grid min-h-0 flex-1 grid-cols-1 lg:grid-cols-[minmax(0,1fr)_22rem]">
        <div className="relative flex min-h-[28rem] flex-col">
          {isCloud ? (
            <CloudArea
              cloud={cloud}
              hasCloud={!!primaryCloud}
              compare={compare}
              setParams={setParams}
              canvasRef={cloudRef}
              view={{
                panes: viewPanes,
                arrangement,
                compare,
                showLinks,
                onShowLinks: setShowLinks,
                last,
                t,
                halfLifeS: hl,
                windowMs: windowMin * 60_000,
                live: replayT === null,
                events,
                filter,
                theme,
                selected,
                onSelect: setSelected,
                hits,
                hitQuery: params.get('q'),
              }}
            />
          ) : (
            <GraphArea
              graph={graph}
              canvasRef={canvasRef}
              styleFor={styleFor}
              selected={selected}
              onSelect={setSelected}
              onHover={(n, x, y) => setHover(n ? { n, x, y } : null)}
              frameKey={t}
              brains={brains}
              hl={hl}
              hover={hover}
              last={last}
              stale={stale}
            />
          )}
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
        <ActivitySide
          selected={selected}
          accesses={selectedAccesses}
          hottest={hottest}
          brains={brains}
          devDetails={devDetails}
          replayAt={replayT}
          onPick={pick}
        />
      </div>
    </div>
  );
}

/** Replay: advance 30× real time; stop at the live edge. */
function useReplay(
  playing: boolean,
  from: number,
  setReplayT: (f: (cur: number | null) => number | null) => void,
  setPlaying: (on: boolean) => void,
) {
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
  }, [playing, from, setReplayT, setPlaying]);
}

/** How many memories sit in a brain's context now. */
function inContextCount(data: MemoryActivityResponse | undefined): number {
  return (data?.inContext ?? []).filter((c) => c.positionFromEnd !== null && c.positionFromEnd !== undefined).length;
}

/** A graph node's label and layer for the most-active list. */
function graphInfo(idx: ReturnType<typeof indexGraph> | null, id: number): Hot['n'] {
  const g = idx?.byId.get(id);
  return g ? { label: g.label, layer: g.layer } : undefined;
}

/** A node on the activity map: its kind colour, a glow for recent use (damped by layer), a ring for its context staleness. */
function nodeStyle(n: GraphNode, la: LastAccess | undefined, s: Staleness | undefined, t: number, hl: number, brains: readonly string[]): NodeStyle {
  const h = heatAt(la, t, hl);
  return {
    fill: n.color,
    alpha: 0.14 + toNumber(n.opacity) * 0.12,
    // Ancestors carry the propagated heat; damp it by layer so a region glows without washing out the centre.
    glow: h > 0.02 && la ? { color: brainColor(la.brain, brains), strength: h * (LAYER_GLOW[n.layer] ?? 0.2) } : null,
    ring: s ? { color: s.evicted ? '#8a929c' : stalenessColor(s.stalenessS), width: 0.4 + stalenessShare(s.stalenessS) * 0.6, hollow: s.evicted } : null,
  };
}

export default function MemoryActivityPage() {
  return (
    <Suspense fallback={null}>
      <ActivityInner />
    </Suspense>
  );
}
