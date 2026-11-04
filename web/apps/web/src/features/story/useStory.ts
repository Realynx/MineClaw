'use client';

/**
 * Story Mode data: the colony list, one colony's state (fetched in full once, then kept current by the stream's `story`
 * deltas and a cheap `since=rev` catch-up every few seconds), the recipe book, and the writes (each followed by a catch-up so
 * the map answers at once).
 */
import { useQuery, useQueryClient } from '@tanstack/react-query';
import type { RouteId, StoryColonyState, StoryRecipe } from '@mineclaw/contracts';
import { useCallback, useEffect } from 'react';
import { api, type RouteBody } from '@/lib/api/client';
import { qk } from '@/lib/api/query';
import { mergeColony } from './model';

export const colonyKey = (id: string) => qk('story.get', { id });

export function useColonies() {
  return useQuery({
    queryKey: qk('story.list'),
    queryFn: ({ signal }) => api('story.list', { signal }),
    staleTime: 10_000,
    refetchInterval: 15_000,
  });
}

/** Catches the colony up from its revision (merged like a stream frame). */
export async function catchUp(qc: ReturnType<typeof useQueryClient>, id: string): Promise<void> {
  const prev = qc.getQueryData<StoryColonyState>(colonyKey(id));
  const d = await api('story.get', { params: { id }, query: prev ? { since: prev.rev } : {} });
  qc.setQueryData<StoryColonyState>(colonyKey(id), (p) => mergeColony(p, prev ? d : { ...d, full: true }));
}

export function useColony(id: string | null) {
  const qc = useQueryClient();
  const q = useQuery<StoryColonyState>({
    queryKey: colonyKey(id ?? ''),
    queryFn: async ({ signal }) => ({ ...(await api('story.get', { params: { id: id! }, signal })), full: true }),
    enabled: !!id,
    staleTime: Infinity,
    refetchOnWindowFocus: false,
  });
  // The stream's `story` frames keep it current; this catch-up covers a quiet or reconnecting stream, and a full refetch
  // every minute is the backstop for anything a delta could miss.
  useEffect(() => {
    if (!id) return;
    const t = setInterval(() => {
      if (document.visibilityState === 'visible') void catchUp(qc, id).catch(() => undefined);
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

type ColonyWrite =
  | 'story.release'
  | 'story.resume'
  | 'story.online'
  | 'story.pawns'
  | 'story.pause'
  | 'story.home'
  | 'story.beds'
  | 'story.priorities'
  | 'story.schedule'
  | 'story.draft'
  | 'story.orders'
  | 'story.zones'
  | 'story.designations'
  | 'story.builds'
  | 'story.storage'
  | 'story.bills'
  | 'story.workbenches'
  | 'story.options';

/**
 * The Architect palette (placeables by tab, tier, unlock state, stock). Refreshed with the colony's revision while the
 * Architect is open: designating a bench or storing materials changes it.
 */
export function useArchitect(id: string | null, rev: number | undefined, open: boolean) {
  return useQuery({
    queryKey: [...qk('story.architect', { id: id ?? '' }), Math.floor((rev ?? 0) / 20)],
    queryFn: ({ signal }) => api('story.architect', { params: { id: id! }, signal }),
    enabled: !!id && open,
    staleTime: 5_000,
    placeholderData: (prev) => prev,
  });
}

/** `write('story.orders', body)` for the current colony; resolves to the route's reply and catches the state up. */
export function useStoryWrite(id: string | null) {
  const qc = useQueryClient();
  return useCallback(
    async <K extends ColonyWrite>(route: K, body: RouteBody<K>) => {
      if (!id) throw new Error('no colony');
      const out = await api(route, { params: { id }, body });
      void catchUp(qc, id).catch(() => undefined);
      // What the colony can make changes with its benches, its storage and its queue: the Architect's tiers follow.
      if (
        route === 'story.workbenches' ||
        route === 'story.options' ||
        route === 'story.builds' ||
        route === 'story.storage'
      )
        void qc.invalidateQueries({ queryKey: qk('story.architect', { id }) });
      return out;
    },
    [qc, id],
  );
}

export type StoryWrite = ReturnType<typeof useStoryWrite>;
export type { RouteId };
