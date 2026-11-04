'use client';

/**
 * The memory activity page's state (app/(app)/memory-activity/page.tsx): what the URL says (whose activity, the layout, the
 * selected node, search hits, the window, the cloud's arrangement or comparison, links), the cloud panes and the heat derived
 * from the bridge's last-access table and the live events.
 */
import { type CloudArrangement, type GraphLayout, type MemoryAccessEvent, type MemoryCloudResponse } from '@mineclaw/contracts';
import { useRouter, useSearchParams } from 'next/navigation';
import { useMemo, useState } from 'react';
import type { CloudViewPane } from './cloud/CloudView';
import { DEFAULT_ARRANGEMENT, cloudParents, isArrangement, parseCompare } from './cloud/cloudStyle';
import { parseFilter, type ActivityFilter } from './heat';
import { useApi } from '@/lib/api/query';

export const WINDOWS = [5, 15, 30, 60] as const;
/** The map layouts (server-laid-out RAPTOR graph) plus the memory cloud (bridge-laid-out arrangements of the leaf embeddings). */
export type View = GraphLayout | 'cloud';
const VIEWS: readonly View[] = ['radial', 'icicle', 'nodelink', 'cloud'];
const DIGITS = /^[0-9]+$/;

/** The page's URL state and its setters (each a `router.replace`, no scroll). */
export function useActivityParams() {
  const params = useSearchParams();
  const router = useRouter();
  const whoKey = params.get('who') ?? 'all';
  const filter = useMemo(() => parseFilter(whoKey), [whoKey]);
  const layoutParam = params.get('layout') as View | null;
  const layout: View = layoutParam && VIEWS.includes(layoutParam) ? layoutParam : 'radial';
  const nodeParam = params.get('node');
  const selected = nodeParam !== null && DIGITS.test(nodeParam) ? Number(nodeParam) : null;
  const hits = useMemo(() => new Set((params.get('hits') ?? '').split(',').filter((x) => DIGITS.test(x)).map(Number)), [params]);
  const windowMin = Number(params.get('window') ?? 15) as (typeof WINDOWS)[number];
  const setParams = (patch: Record<string, string | null>) => {
    const sp = new URLSearchParams(params.toString());
    for (const [k, v] of Object.entries(patch)) {
      if (v === null) sp.delete(k);
      else sp.set(k, v);
    }
    router.replace(`/memory-activity?${sp.toString()}`, { scroll: false });
  };
  const setParam = (k: string, v: string | null) => setParams({ [k]: v });
  const setSelected = (id: number | null) => setParam('node', id === null ? null : String(id));
  return { params, filter, layout, selected, hits, windowMin, setParams, setParam, setSelected };
}

/** Links on the cloud: the URL's `links` (0 / 1) wins, else the choice remembered in this browser (on by default). */
export function useShowLinks(linksParam: string | null, setParam: (k: string, v: string | null) => void) {
  const [linksPref, setLinksPref] = useState<boolean>(() => {
    try {
      return typeof window === 'undefined' || window.localStorage.getItem('mcx-cloud-links') !== '0';
    } catch {
      return true;
    }
  });
  const showLinks = linksParam === '0' ? false : linksParam === '1' ? true : linksPref;
  const setShowLinks = (on: boolean) => {
    setLinksPref(on);
    try {
      window.localStorage.setItem('mcx-cloud-links', on ? '1' : '0');
    } catch {
      // private window: the URL still keeps it
    }
    setParam('links', on ? null : '0');
  };
  return { showLinks, setShowLinks };
}

/**
 * The cloud's panes: `arr` (one pane, default the distance layout) or `cmp` (2–4 side by side). A pane shows its arrangement
 * once it has points; until then the last thing it showed (so a switch animates instead of flashing a spinner).
 */
export function useCloudPanes(params: URLSearchParams | { get(k: string): string | null }, isCloud: boolean) {
  const arrParam = params.get('arr');
  const arrangement: CloudArrangement = isArrangement(arrParam) ? arrParam : DEFAULT_ARRANGEMENT;
  const compare = parseCompare(params.get('cmp'));
  const paneArrangements: readonly CloudArrangement[] = compare ?? [arrangement];
  const cloudOpts = (a: CloudArrangement) => ({
    enabled: isCloud && paneArrangements.includes(a),
    // The bridge lays each arrangement out in the background: poll while it does.
    refetchInterval: (q: { state: { data?: { status: string } } }) => (q.state.data?.status === 'computing' ? 2000 : 60_000),
  });
  const cloudQ = {
    mds: useApi('memoryActivity.cloud', { query: { arrangement: 'mds' } }, cloudOpts('mds')),
    umap: useApi('memoryActivity.cloud', { query: { arrangement: 'umap' } }, cloudOpts('umap')),
    pca: useApi('memoryActivity.cloud', { query: { arrangement: 'pca' } }, cloudOpts('pca')),
    tree: useApi('memoryActivity.cloud', { query: { arrangement: 'tree' } }, cloudOpts('tree')),
  } satisfies Record<CloudArrangement, unknown>;
  const [lastShown, setLastShown] = useState<ReadonlyArray<MemoryCloudResponse | null>>([]);
  const viewPanes: CloudViewPane[] = paneArrangements.map((a, slot) => {
    const d = cloudQ[a].data;
    const ready = d && d.points.id.length > 0 ? d : null;
    const fallback = ready ?? lastShown[slot] ?? lastShown[0] ?? null;
    return { arrangement: a, cloud: fallback, pending: !ready || ready.status === 'computing' };
  });
  if (viewPanes.length !== lastShown.length || viewPanes.some((p, i) => p.cloud !== lastShown[i]))
    setLastShown(viewPanes.map((p) => p.cloud));
  return { arrangement, compare, viewPanes, cloud: cloudQ[paneArrangements[0]!], primaryCloud: viewPanes[0]!.cloud };
}

export type CloudQuery = ReturnType<typeof useCloudPanes>['cloud'];

/** The cloud's parents and per-node label / layer (leaves layer 0; a summary labelled by its layer and cluster). */
export function useCloudIndex(primaryCloud: MemoryCloudResponse | null, isCloud: boolean) {
  return useMemo(() => {
    const c = isCloud ? (primaryCloud ?? undefined) : undefined;
    if (!c) return null;
    const info = new Map<number, { label: string; layer: number }>();
    c.points.id.forEach((id, i) => info.set(id, { label: c.points.title[i]!, layer: 0 }));
    c.summaries.id.forEach((id, i) => {
      const cl = c.summaries.cluster[i]!;
      info.set(id, { label: `L${c.summaries.layer[i]} · ${cl >= 0 ? (c.clusters[cl]?.label ?? '') : 'summary'}`, layer: c.summaries.layer[i]! });
    });
    return { parents: cloudParents(c), info };
  }, [primaryCloud, isCloud]);
}

/** The brains and bodies to filter by: the heat table's brains and every brain / body in the events, sorted. */
export function useWho(heatBrains: readonly string[] | undefined, events: readonly MemoryAccessEvent[]) {
  const brains = useMemo(() => [...new Set([...(heatBrains ?? []), ...events.map((e) => e.brain)])].sort(), [heatBrains, events]);
  const bodies = useMemo(() => [...new Set(events.map((e) => e.body).filter((b): b is string => !!b))].sort(), [events]);
  return { brains, bodies };
}

/** A body filter's brain (the body's events name it), for the heat table's per-brain rows. */
export function bodyBrainOf(filter: ActivityFilter, events: readonly MemoryAccessEvent[]): string | undefined {
  return filter.kind === 'body' ? events.find((e) => e.body === filter.body)?.brain : undefined;
}
