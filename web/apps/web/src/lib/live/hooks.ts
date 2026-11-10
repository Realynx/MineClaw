'use client';

/**
 * Hooks over the live topics. Each fetches its REST route once (and again on resync) and merges the result with whatever the
 * stream has already put in the cache, so no frame is lost between the fetch and the first stream payload.
 */
import { useQuery, useQueryClient } from '@tanstack/react-query';
import type {
  BotNow,
  EventsResponse,
  FeedResponse,
  HiveTreeNode,
  MemoryActivityResponse,
  StatusResponse,
  WorldEntities,
} from '@mineclaw/contracts';
import { api, type ApiError } from '../api/client';
import { liveKeys } from './dispatch';
import { mergeEvents, mergeFeed, mergeMemoryActivity, seedEntities } from './merge';

const live = { staleTime: Infinity, refetchOnWindowFocus: false } as const;

export function useStatus() {
  return useQuery<StatusResponse, ApiError>({
    queryKey: liveKeys.status,
    queryFn: ({ signal }) => api('status', { signal }),
    ...live,
  });
}

export function useNow(bot: string | null | undefined) {
  return useQuery<BotNow, ApiError>({
    queryKey: liveKeys.now(bot ?? ''),
    queryFn: ({ signal }) => api('bots.now', { params: { bot: bot ?? '' }, signal }),
    enabled: !!bot,
    ...live,
  });
}

export function useFeed() {
  const qc = useQueryClient();
  return useQuery<FeedResponse, ApiError>({
    queryKey: liveKeys.feed,
    queryFn: async ({ signal }) => {
      const fetched = await api('feed', { query: { since: 0 }, signal });
      return mergeFeed(qc.getQueryData<FeedResponse>(liveKeys.feed), fetched);
    },
    ...live,
  });
}

export function useEvents() {
  const qc = useQueryClient();
  return useQuery<EventsResponse, ApiError>({
    queryKey: liveKeys.events,
    queryFn: async ({ signal }) => {
      const fetched = await api('events', { query: { since: 0 }, signal });
      return mergeEvents(qc.getQueryData<EventsResponse>(liveKeys.events), fetched);
    },
    ...live,
  });
}

export function useEntities() {
  const qc = useQueryClient();
  return useQuery<WorldEntities, ApiError>({
    queryKey: liveKeys.entities,
    queryFn: async ({ signal }) => {
      const fetched = await api('world.entities', { query: { since: 0 }, signal });
      // Bot poses advance independently of the entity revision. A slow resync must not rewind a newer stream pose just
      // because no mob changed revision while the REST request was in flight; a newer set that began with a delta is laid
      // over the snapshot instead of kept on its own (it lacks every entity that did not change).
      return seedEntities(fetched, qc.getQueryData<WorldEntities>(liveKeys.entities));
    },
    ...live,
  });
}

export function useHiveTree() {
  return useQuery<HiveTreeNode, ApiError>({
    queryKey: liveKeys.hive,
    queryFn: ({ signal }) => api('hive.tree', { signal }),
    ...live,
  });
}

export function useMemoryActivity() {
  const qc = useQueryClient();
  return useQuery<MemoryActivityResponse, ApiError>({
    queryKey: liveKeys.memoryActivity,
    queryFn: async ({ signal }) => {
      const fetched = await api('memoryActivity.list', { query: { since: 0 }, signal });
      return mergeMemoryActivity(qc.getQueryData<MemoryActivityResponse>(liveKeys.memoryActivity), fetched);
    },
    ...live,
  });
}

/** Ticks every `ms` (for elapsed timers and heat decay); returns Date.now(). */
export { useTicker } from './ticker';
