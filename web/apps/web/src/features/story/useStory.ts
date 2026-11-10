'use client';

/**
 * Story Mode data: the colony list, one colony's state (fetched in full once, then kept current by the stream's `story`
 * deltas and a cheap `since=rev` catch-up every few seconds), the recipe book, and the writes (each followed by a catch-up so
 * the map answers at once).
 */
import { useQuery, useQueryClient } from '@tanstack/react-query';
import type { RouteId, StoryColonyState, StoryRecipe } from '@mineclaw/contracts';
import { useCallback, useEffect } from 'react';
import { api, ApiError, type RouteBody } from '@/lib/api/client';
import { qk } from '@/lib/api/query';
import { catchingUp, catchUp, colonyKey, readColony } from './colonySync';
import { storyFramesFlowing } from './frames';
import { catchUpAfterWrite, sendColonyWrite } from './optimistic';

// The colony state's cache key and its catch-up live in colonySync.ts (the stream uses them too).
export { catchUp, colonyKey };

export function useColonies() {
  return useQuery({
    queryKey: qk('story.list'),
    queryFn: ({ signal }) => api('story.list', { signal }),
    staleTime: 10_000,
    refetchInterval: 15_000,
  });
}

/** Colony queries whose missing state is being asked for again: one request each at a time (it may wait a while). */
const refetching = new WeakSet<object>();

/**
 * A colony whose first fetch failed has no state for the stream's deltas to merge into, and the `since=rev` catch-up
 * stands down while those frames flow: asks for the whole state again, whether or not frames flow. True when it is
 * being asked for (the caller's catch-up is not needed too). It asks through `catchUp` so the failed query keeps saying
 * it failed until a state arrives (a query refetch turns it back to "loading" for every attempt, and the page's "can't
 * reach the colony" would blink). A colony the server does not have (404) is not asked for here or by the catch-up (true):
 * the page shows the found-a-colony card, and the minute's full refetch still notices one that appears.
 */
export function refetchMissing(qc: ReturnType<typeof useQueryClient>, id: string): boolean {
  if (document.visibilityState !== 'visible') return false;
  const query = qc.getQueryCache().find({ queryKey: colonyKey(id) });
  if (!query || query.state.data !== undefined || query.state.fetchStatus !== 'idle') return false;
  if (query.state.error instanceof ApiError && query.state.error.status === 404) return true;
  if (refetching.has(query)) return true;
  refetching.add(query);
  void catchUp(qc, id)
    .catch(() => undefined)
    .finally(() => refetching.delete(query));
  return true;
}

export function useColony(id: string | null) {
  const qc = useQueryClient();
  const q = useQuery<StoryColonyState>({
    queryKey: colonyKey(id ?? ''),
    queryFn: ({ signal }) => readColony(qc, id!, signal),
    enabled: !!id,
    staleTime: Infinity,
    refetchOnWindowFocus: false,
  });
  // The stream's `story` frames keep it current; this catch-up covers a quiet or reconnecting stream (it skips while frames
  // flow), and a full refetch every minute is the backstop for anything a delta could miss.
  // The catch-up also skips while an earlier one is still on the wire: a slow bot must not collect one more read every 3 s.
  useEffect(() => {
    if (!id) return;
    const t = setInterval(() => {
      if (refetchMissing(qc, id)) return;
      if (document.visibilityState === 'visible' && !storyFramesFlowing(id) && !catchingUp(qc, id))
        void catchUp(qc, id).catch(() => undefined);
    }, 3000);
    const full = setInterval(() => void qc.invalidateQueries({ queryKey: colonyKey(id) }), 60_000);
    return () => {
      clearInterval(t);
      clearInterval(full);
    };
  }, [qc, id]);
  return q;
}

export function useRecipes(workbench: string | null) {
  return useQuery<StoryRecipe[]>({
    queryKey: qk('story.recipes', {}, { workbench }),
    queryFn: ({ signal }) =>
      api('story.recipes', { query: workbench ? { workbench: workbench as never } : {}, signal }),
    enabled: !!workbench,
    staleTime: 5 * 60_000,
  });
}

export type ColonyWrite =
  | 'story.release'
  | 'story.resume'
  | 'story.online'
  | 'story.pawns'
  | 'story.pause'
  | 'story.home'
  | 'story.beds'
  | 'story.priorities'
  | 'story.roles'
  | 'story.schedule'
  | 'story.draft'
  | 'story.orders'
  | 'story.zones'
  | 'story.designations'
  | 'story.builds'
  | 'story.storage'
  | 'story.bills'
  | 'story.workbenches'
  | 'story.options'
  | 'story.policies'
  | 'story.materialRules'
  | 'story.gear';

/**
 * The Architect palette (placeables by tab, tier, unlock state, stock), fetched while the Architect is open. The state's
 * `architectKey` folds everything the palette reads (free stock, benches, version): the palette is fetched again only when it
 * moves (live 11-04 the old key, the colony revision / 20, refetched it every ~4 s at ~200 ms each). A bot without the key
 * falls back to the revision buckets.
 */
export function useArchitect(id: string | null, rev: number | undefined, open: boolean, architectKey?: number | null) {
  const key = architectKey ?? `rev:${Math.floor((rev ?? 0) / 20)}`;
  return useQuery({
    queryKey: [...qk('story.architect', { id: id ?? '' }), key],
    queryFn: ({ signal }) => api('story.architect', { params: { id: id! }, signal }),
    enabled: !!id && open,
    staleTime: architectKey === null || architectKey === undefined ? 5_000 : Infinity,
    placeholderData: (prev) => prev,
  });
}

/** `write('story.orders', body)` for the current colony; resolves to the route's reply and catches the state up. */
export function useStoryWrite(id: string | null) {
  const qc = useQueryClient();
  return useCallback(
    async <K extends ColonyWrite>(route: K, body: RouteBody<K>) => {
      if (!id) throw new Error('no colony');
      // The request, its line in the command ring and the Architect palette: `sendColonyWrite` (optimistic.ts).
      const out = await sendColonyWrite(qc, id, route, body);
      // With `rev` while frames flow, the stream brings the change (one read only if it does not); else one read now.
      void catchUpAfterWrite(qc, id, out);
      return out;
    },
    [qc, id],
  );
}

export type StoryWrite = ReturnType<typeof useStoryWrite>;
export type { RouteId };
